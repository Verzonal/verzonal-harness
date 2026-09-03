using System.Diagnostics.CodeAnalysis;
using Dsh.Agent;
using Dsh.Cordis;
using Dsh.Llm;
using Dsh.Session;
using Dsh.SystemPrompt;
using Dsh.Tools;

namespace Dsh.AgentLoop;

/// <summary>
/// The default agent driver: one session, driven through turns and steps, with every
/// request derived from the log.
/// </summary>
/// <remarks>
/// The machine has three phases. <c>Idle</c> owns nothing. <c>Running</c> owns a turn
/// and its cancellation. <c>Maintenance</c> is work that needs the agent to itself —
/// compaction, say — and reads as idle from outside, because it is not a model turn.
///
/// Two rules shape everything else. Every exit path commits <c>turn/end</c>, so a
/// turn that was rejected, cancelled, or failed is as durably closed as one that
/// completed. And whether the turn continues is decided by <em>data</em> — what the
/// inbox holds after the objection window — never by what a listener returned, so no
/// listener ordering can change the outcome.
/// </remarks>
public sealed class ReactLoopAgent : IAgent
{
    private enum PhaseKind
    {
        Idle,
        Maintenance,
        Running,
    }

    private sealed class Phase
    {
        public PhaseKind Kind { get; set; } = PhaseKind.Idle;
        public CancellationTokenSource? Abort { get; set; }
        public AgentCancelCause? Cause { get; set; }
        public int LastTurn { get; set; }
        public int Turn { get; set; }
        public int Step { get; set; }
        public bool WakeRequested { get; set; }
    }

    private sealed record PreparedStep(PreStepDecision Decision, PromptAssembly? Assembly);

    private readonly Context _loopCtx;
    private readonly LlmRuntime _llm;
    private readonly SystemPromptService _prompt;
    private readonly ToolRuntime _tools;
    private readonly int _maxParallelToolCalls;
    private readonly object _gate = new();
    private readonly RuntimeContextProjection _runtimeContext = new();

    private Phase _phase;
    private Task _activity = Task.CompletedTask;
    private bool _requestHeaderLogged;

    /// <param name="loopCtx">The factory's context, which owns the driver's dispatches.</param>
    /// <param name="session">The session to drive.</param>
    /// <param name="options">The route this agent's requests use.</param>
    /// <param name="maxParallelToolCalls">How many overlapping tool calls the pool allows.</param>
    public ReactLoopAgent(Context loopCtx, Session.Session session, AgentOptions options, int maxParallelToolCalls)
    {
        _loopCtx = loopCtx;
        Session = session;
        Options = options;
        _maxParallelToolCalls = maxParallelToolCalls;

        _llm = loopCtx.Require<LlmRuntime>(LlmKeys.Service);
        _prompt = loopCtx.Require<SystemPromptService>(SystemPromptKeys.Service);
        _tools = loopCtx.Require<ToolRuntime>(ToolKeys.Service);

        Scope = new ScopeKey($"agent:{session.Id}");
        Ctx = loopCtx.WithScope(Scope).Extend("agent", this);

        Inbox = new Inbox(session)
        {
            Inserted = message => Emit(AgentKeys.InboxInserted, new AgentInboxNotice(this, message)),
            Discarded = message => Emit(AgentKeys.InboxDiscarded, new AgentInboxNotice(this, message)),
            Claimed = (message, turn) => Emit(AgentKeys.InboxClaimed, new AgentInboxNotice(this, message, turn)),
        };

        _phase = new Phase { Kind = PhaseKind.Idle, LastTurn = session.LastTurn() };
    }

    /// <inheritdoc />
    public SessionId Id => Session.Id;

    /// <inheritdoc />
    public AgentOptions Options { get; }

    /// <inheritdoc />
    public Session.Session Session { get; }

    /// <inheritdoc />
    public Inbox Inbox { get; }

    /// <inheritdoc />
    public Context Ctx { get; }

    /// <inheritdoc />
    public ScopeKey Scope { get; }

    /// <inheritdoc />
    public AgentStatus Status
    {
        get
        {
            lock (_gate) return _phase.Kind == PhaseKind.Running ? AgentStatus.Running : AgentStatus.Idle;
        }
    }

    private void Emit<T>(EmitKey<T> key, T payload) => _loopCtx.Emit(key, payload, Scope);

    private void SetPhase(Action<Phase> mutate)
    {
        AgentStatus before;
        AgentStatus after;
        lock (_gate)
        {
            before = _phase.Kind == PhaseKind.Running ? AgentStatus.Running : AgentStatus.Idle;
            mutate(_phase);
            after = _phase.Kind == PhaseKind.Running ? AgentStatus.Running : AgentStatus.Idle;
        }

        if (before != after) Emit(AgentKeys.Status, new AgentStatusNotice(this, after));
    }

    /// <inheritdoc />
    public void Send(Message message, InboxTarget target, bool wakeup)
    {
        bool wakingAfterAbort;
        lock (_gate)
        {
            // Waking input cannot join an activity that is already unwinding, so it
            // opens the next turn instead. Classified before the insertion, so a
            // cancellation triggered by an observer cannot reclassify it.
            wakingAfterAbort = wakeup
                && _phase.Kind != PhaseKind.Idle
                && _phase.Abort?.IsCancellationRequested == true;
        }

        var resolved = wakingAfterAbort ? InboxTarget.NextTurn : target;
        Inbox.Append(resolved, message);
        if (wakeup) WakeDriver(wakingAfterAbort);
    }

    /// <inheritdoc />
    public void Followup(Message message) => Send(message, InboxTarget.NextTurn, true);

    /// <inheritdoc />
    public void Steer(Message message) => Send(message, InboxTarget.NextStep, true);

    /// <inheritdoc />
    public void Inject(Message message) => Send(message, InboxTarget.NextStep, false);

    /// <inheritdoc />
    public void Cancel(AgentCancelCause cause, CancelOptions? options = null)
    {
        if (options?.KeepInbox != true)
        {
            Inbox.Clear();
            lock (_gate)
            {
                if (_phase.Kind != PhaseKind.Idle) _phase.WakeRequested = false;
            }
        }

        CancellationTokenSource? abort;
        lock (_gate)
        {
            if (_phase.Kind == PhaseKind.Idle) return;
            _phase.Cause = cause;
            abort = _phase.Abort;
        }

        abort?.Cancel();
    }

    /// <inheritdoc />
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task activity;
            lock (_gate) activity = _activity;
            try
            {
                await activity;
            }
            catch (Exception error)
            {
                _loopCtx.Logger.Log(LogLevel.Debug, "agent-loop", $"agent \"{Id}\" activity ended", error);
            }

            lock (_gate)
            {
                if (ReferenceEquals(activity, _activity)) return;
            }
        }
    }

    /// <inheritdoc />
    public Task<T> RunMaintenanceAsync<T>(Func<CancellationToken, Task<T>> job)
    {
        var completion = new TaskCompletionSource();
        CancellationTokenSource abort;

        lock (_gate)
        {
            if (_phase.Kind != PhaseKind.Idle)
            {
                throw new InvalidOperationException($"agent \"{Id}\" already has active work");
            }

            abort = new CancellationTokenSource();
            _phase.Kind = PhaseKind.Maintenance;
            _phase.Abort = abort;
            _phase.WakeRequested = false;
            _activity = completion.Task;
        }

        return RunAsync();

        async Task<T> RunAsync()
        {
            try
            {
                return await job(abort.Token);
            }
            finally
            {
                bool replay;
                lock (_gate)
                {
                    replay = _phase.WakeRequested;
                    _phase.Kind = PhaseKind.Idle;
                    _phase.Abort = null;
                    _phase.WakeRequested = false;
                }

                completion.SetResult();
                abort.Dispose();
                if (replay && Inbox.HasPending) WakeDriver();
            }
        }
    }

    /// <summary>
    /// Start a driver, or remember that one is owed.
    /// </summary>
    /// <param name="wakeAfterAbort">
    /// Whether the waking message arrived after the current activity had already been
    /// cancelled, classified by the caller before it touched the inbox.
    /// </param>
    private void WakeDriver(bool wakeAfterAbort = false)
    {
        TaskCompletionSource completion;

        lock (_gate)
        {
            if (_phase.Kind != PhaseKind.Idle)
            {
                // A live driver claims new work itself. Maintenance and an already
                // cancelled turn cannot, so the wake is latched and replayed when they
                // converge — unless the agent is being torn down, in which case
                // teardown must not wait on a fresh model turn.
                if (_phase.Cause is not DisposedCancel
                    && (_phase.Kind == PhaseKind.Maintenance || wakeAfterAbort))
                {
                    _phase.WakeRequested = true;
                }

                return;
            }

            completion = new TaskCompletionSource();
            _activity = completion.Task;
            _phase.Kind = PhaseKind.Running;
            _phase.Abort = new CancellationTokenSource();
            _phase.Cause = null;
            _phase.Turn = _phase.LastTurn;
            _phase.Step = 0;
            _phase.WakeRequested = false;
        }

        Emit(AgentKeys.Status, new AgentStatusNotice(this, AgentStatus.Running));

        _ = Task.Run(async () =>
        {
            try
            {
                await AgentRegistry.WithInitiatorAsync(this, KickAsync);
            }
            finally
            {
                completion.SetResult();
            }
        });
    }

    private async Task KickAsync()
    {
        try
        {
            while (await TurnAsync()) { }
        }
        catch (Exception error)
        {
            // Cancellation and already-reported failures are contained here: a plugin
            // failure ends the turn, never the agent.
            _loopCtx.Logger.Log(LogLevel.Debug, "agent-loop", $"agent \"{Id}\" turn ended", error);
        }
        finally
        {
            bool replay;
            lock (_gate)
            {
                replay = _phase.WakeRequested;
                _phase.LastTurn = _phase.Turn;
                _phase.Kind = PhaseKind.Idle;
                _phase.Abort?.Dispose();
                _phase.Abort = null;
                _phase.WakeRequested = false;
            }

            Emit(AgentKeys.Status, new AgentStatusNotice(this, AgentStatus.Idle));
            if (replay && Inbox.HasPending) WakeDriver();
        }
    }

    [DoesNotReturn]
    private void ReportAndThrow(Exception error)
    {
        int turn;
        int step;
        lock (_gate)
        {
            turn = _phase.Kind == PhaseKind.Running ? _phase.Turn : _phase.LastTurn;
            step = _phase.Kind == PhaseKind.Running ? _phase.Step : 0;
        }

        Emit(AgentKeys.Error, new AgentErrorNotice(this, turn, step, error));
        throw error;
    }

    private async Task<PreparedStep> PreStepAsync(InboxTarget target, int turn, int step, CancellationToken token)
    {
        var claimed = Inbox.Claim(target, turn);
        var assembly = await _prompt.AssembleAsync(new AssembleContext(Scope, token));
        token.ThrowIfCancellationRequested();

        var sections = SystemPromptService.RenderContextSections(assembly);
        var snapshot = _runtimeContext.Project(SystemPromptService.JoinContextSections(sections));

        var decision = await _loopCtx.WaterfallAsync(
            AgentKeys.PreStep,
            new PreStepPayload(this, claimed, turn, step, token),
            () => Task.FromResult<PreStepDecision>(new EnterStep(
                snapshot is null ? claimed : [.. claimed, snapshot])),
            Scope);

        token.ThrowIfCancellationRequested();
        return new PreparedStep(decision, assembly);
    }

    private async Task<bool> TurnAsync()
    {
        Phase phase;
        CancellationToken token;
        lock (_gate)
        {
            phase = _phase;
            token = phase.Abort!.Token;
        }

        token.ThrowIfCancellationRequested();

        var turn = phase.Turn + 1;
        try
        {
            Session.Append(SessionEvents.TurnStart, new TurnStartData(turn));
        }
        catch (Exception error)
        {
            ReportAndThrow(error);
        }

        phase.Turn = turn;
        TurnEndReason? turnEnds = null;
        var target = InboxTarget.NextTurn;

        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var step = phase.Step + 1;
                var prepared = await PreStepAsync(target, turn, step, token);

                if (prepared.Decision is RejectStep)
                {
                    turnEnds = BlockedTurnEnd.Instance;
                    return false;
                }

                var entering = ((EnterStep)prepared.Decision).Messages;

                if (turnEnds is not null && entering.Count == 0) break;

                // A waking message that was removed, or an enter decision rewritten to
                // nothing, still owns the turn boundary it opened — it just spends no
                // model call.
                if (phase.Step == 0 && entering.Count == 0)
                {
                    turnEnds = CompletedTurnEnd.Instance;
                    return false;
                }

                token.ThrowIfCancellationRequested();
                Session.Append(SessionEvents.StepStart, new StepStartData(turn, step));
                phase.Step = step;

                try
                {
                    foreach (var message in entering)
                    {
                        Session.Append(SessionEvents.UserMessage, message, new SurfaceIntent(AppendOp.Instance));
                    }

                    var stepEnd = await StepAsync(prepared.Assembly!, turn, step, token);

                    // max-tokens is sticky: a later step completing normally must not
                    // downgrade what the turn reports.
                    if (turnEnds is not MaxTokensTurnEnd) turnEnds = stepEnd;
                }
                finally
                {
                    Session.Append(SessionEvents.StepEnd, new StepEndData(turn, step));
                }

                token.ThrowIfCancellationRequested();

                if (turnEnds is not null && Inbox.NextStep.Count == 0)
                {
                    await _loopCtx.SerialAsync(
                        AgentKeys.TurnStopping,
                        new TurnStoppingPayload(this, turn, token),
                        Scope);
                    token.ThrowIfCancellationRequested();
                }

                // Read a second time on purpose: an objector steers rather than
                // returning a veto, so the inbox is what decides.
                if (turnEnds is not null && Inbox.NextStep.Count == 0) break;
                target = InboxTarget.NextStep;
            }
        }
        catch (Exception error)
        {
            if (token.IsCancellationRequested)
            {
                turnEnds = new AbortedTurnEnd(phase.Cause ?? UserCancel.Instance);
                throw;
            }

            turnEnds = new ErrorTurnEnd(LlmFailures.Normalize(error));
            ReportAndThrow(error);
        }
        finally
        {
            try
            {
                Session.Append(
                    SessionEvents.TurnEnd,
                    new TurnEndData(turn, turnEnds ?? CompletedTurnEnd.Instance));
            }
            catch (Exception error)
            {
                _loopCtx.Logger.Log(LogLevel.Error, "agent-loop", $"agent \"{Id}\" could not close turn {turn}", error);
            }
        }

        if (!Inbox.HasPending) return false;

        lock (_gate)
        {
            phase.Abort?.Dispose();
            phase.Abort = new CancellationTokenSource();
            phase.Cause = null;

            // A latch set on the previous controller is stale now: this driver claims
            // the queue itself.
            phase.WakeRequested = false;
            phase.Step = 0;
        }

        return true;
    }

    private async Task<TurnEndReason?> StepAsync(
        PromptAssembly assembly,
        int turn,
        int step,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var system = SystemPromptService.RenderPrompt(assembly);
        var attempt = 0;

        while (true)
        {
            attempt++;
            var (request, prepared) = await BuildRequestAsync(
                turn, step, assembly.Tools, system, Session.DeriveMessages(), token);

            var assembler = new BlockAssembler();
            var chunkSeqs = new List<int>();

            try
            {
                var stream = prepared is not null
                    ? prepared.StreamAsync(request, token)
                    : _llm.StreamAsync(request, token);

                token.ThrowIfCancellationRequested();
                await foreach (var chunk in stream.WithCancellation(token))
                {
                    token.ThrowIfCancellationRequested();
                    chunkSeqs.Add(Session
                        .Append(SessionEvents.AssistantChunk, new AssistantChunkData(turn, step, chunk))
                        .Seq);
                    assembler.Push(chunk);
                }

                token.ThrowIfCancellationRequested();
            }
            catch when (token.IsCancellationRequested)
            {
                // Whatever the model had already delivered is what the person saw, so
                // it is recorded — marked interrupted, with the undispatched tool calls
                // left out because they never ran.
                var delivered = assembler.InterruptedBlocks();
                if (delivered.Count > 0)
                {
                    Session.Append(
                        SessionEvents.AssistantMessage,
                        new AssistantMessageData(
                            turn,
                            step,
                            Message.Assistant(delivered, new ModelMessageSource(request.Provider, request.Model)),
                            assembler.Usage,
                            Interrupted: true),
                        new SurfaceIntent(AppendOp.Instance, chunkSeqs));
                }

                throw;
            }

            var failure = assembler.Finish switch
            {
                ErrorFinish error => error.Failure,
                AbortedFinish abortedFinish => abortedFinish.Failure,
                _ => null,
            };

            if (failure is not null)
            {
                var action = await _loopCtx.WaterfallAsync(
                    AgentKeys.RequestError,
                    new RequestErrorPayload(
                        this, turn, step, request.Provider, failure, prepared?.RetryPolicy, attempt, token),
                    () => Task.FromResult<RequestErrorAction>(TerminalRequestFailure.Instance),
                    Scope);

                token.ThrowIfCancellationRequested();
                if (action is not RetryRequest) throw new LlmError(failure);
                continue;
            }

            var message = Message.Assistant(
                assembler.Blocks(),
                new ModelMessageSource(request.Provider, request.Model, assembler.ReplayState));

            Session.Append(
                SessionEvents.AssistantMessage,
                new AssistantMessageData(turn, step, message, assembler.Usage),
                new SurfaceIntent(AppendOp.Instance, chunkSeqs));

            if (assembler.Finish is MaxTokensFinish) return MaxTokensTurnEnd.Instance;

            var toolCalls = ContentBlocks.ToolCalls(message.Content);
            if (toolCalls.Count == 0) return CompletedTurnEnd.Instance;

            var concluded = await ToolCallScheduler.ExecuteAsync(
                _tools,
                Session,
                Scope,
                turn,
                step,
                toolCalls,
                _maxParallelToolCalls,
                context => Inbox.Append(InboxTarget.NextStep, context),
                token);

            // Tools ran: unless one of them concluded the turn, the model is owed
            // another request with their results in view.
            return concluded ? CompletedTurnEnd.Instance : null;
        }
    }

    /// <summary>
    /// Compose one request and bind it to the adapter that resolved its defaults.
    /// </summary>
    /// <remarks>
    /// The request must be reconstructible from the log alone, so everything that
    /// shapes it — the configuration, the rendered prompt, the tool schemas — is
    /// written to the log before it is dispatched.
    /// </remarks>
    private async Task<(GenerateOptions Request, IPreparedLlmCall? Prepared)> BuildRequestAsync(
        int turn,
        int step,
        IReadOnlyList<ToolSchema> tools,
        string system,
        IReadOnlyList<Message> history,
        CancellationToken token)
    {
        var persisted = Session.RequestHeader();

        LlmCallConfig seed;
        if (_requestHeaderLogged && persisted is not null)
        {
            seed = RequestProposal(persisted);
        }
        else
        {
            // A fresh loop instance starts from its declared route, restoring only a
            // thinking level that this exact route chose for itself.
            var sameRoute = persisted?.Config.Provider == (Options.Provider ?? string.Empty)
                            && persisted?.Config.Model == (Options.Model ?? string.Empty)
                            && persisted?.AdapterDefaults?.ReasoningEffort != true;
            seed = new LlmCallConfig(
                Options.Provider ?? string.Empty,
                Options.Model ?? string.Empty,
                sameRoute ? persisted?.Config.ReasoningEffort : null,
                MaxTokens: Options.MaxTokens);
        }

        var proposed = await _loopCtx.WaterfallAsync(
            AgentKeys.Request,
            new RequestPayload(this, turn, step, token),
            () => Task.FromResult(seed),
            Scope);

        token.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(proposed.Provider) || string.IsNullOrEmpty(proposed.Model))
        {
            throw new InvalidOperationException(
                $"agent \"{Id}\" has no provider/model: set AgentOptions.Provider and AgentOptions.Model, or supply both through the agent/request waterfall");
        }

        LlmCallConfig config;
        IPreparedLlmCall? prepared = null;
        try
        {
            prepared = await _llm.PrepareCallAsync(proposed, token);
            config = prepared.Config;
        }
        catch (LlmError error) when (error.Code == LlmErrorCodes.NoAdapter)
        {
            // Middleware may serve a route no adapter claims; the terminal dispatch
            // will say so if nothing does.
            config = proposed;
        }

        token.ThrowIfCancellationRequested();

        var header = RequestHeaders.Canonical(new EpochHeader(
            config,
            prepared?.AdapterDefaults,
            system,
            tools));

        var baseline = Session.RequestHeader();
        if (!_requestHeaderLogged)
        {
            Session.Append(
                SessionEvents.RequestHeader,
                new RequestHeaderData(
                    header,
                    baseline is null ? RequestHeaderReason.Initial : RequestHeaderReason.Resume));
            _requestHeaderLogged = true;
        }
        else if (!RequestHeaders.Equal(baseline, header))
        {
            Session.Append(SessionEvents.RequestHeader, new RequestHeaderData(header, RequestHeaderReason.Change));
        }

        var routeContext = new RequestContextData(config.Provider, config.Model, prepared?.Context?.ContextWindow);
        if (Session.RequestContext() != routeContext)
        {
            Session.Append(SessionEvents.RequestContext, routeContext);
        }

        token.ThrowIfCancellationRequested();

        var request = new GenerateOptions(
            header.Config,
            history,
            header.System,
            header.Tools,
            Session.Id);

        return (request, prepared);
    }

    /// <summary>
    /// Strip the values the adapter supplied before plugins propose the next request,
    /// so switching routes rematerializes the new route's own defaults instead of
    /// inheriting the previous one's.
    /// </summary>
    private static LlmCallConfig RequestProposal(EpochHeader header)
    {
        if (header.AdapterDefaults is null) return header.Config;
        return header.Config with
        {
            ReasoningEffort = header.AdapterDefaults.ReasoningEffort ? null : header.Config.ReasoningEffort,
            MaxTokens = header.AdapterDefaults.MaxTokens ? null : header.Config.MaxTokens,
        };
    }

    /// <summary>
    /// Emits the runtime-context snapshot only when it actually changed, so an
    /// unchanged environment does not re-enter the model's view every step.
    /// </summary>
    private sealed class RuntimeContextProjection
    {
        private string? _last;

        public Message? Project(string snapshot)
        {
            if (string.Equals(snapshot, _last, StringComparison.Ordinal)) return null;
            _last = snapshot;
            if (snapshot.Length == 0) return null;
            return Message.Context(
                "dsh-system-prompt",
                ContextForm.Snapshot,
                [new TextBlock(snapshot)],
                "Runtime context");
        }
    }
}

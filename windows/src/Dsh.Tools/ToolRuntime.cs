using Dsh.Cordis;
using Dsh.Llm;
using Dsh.Session;

namespace Dsh.Tools;

/// <summary>One tool call being run, with the cancellation that governs it.</summary>
/// <param name="Execution">The call's identity and arguments.</param>
/// <param name="CancellationToken">Cancels the call.</param>
public sealed record ToolExecutionContext(ToolExecution Execution, CancellationToken CancellationToken);

/// <summary>A completed call, for post-execution policy.</summary>
/// <param name="Execution">The call's identity and arguments.</param>
/// <param name="Result">What it produced.</param>
/// <param name="CancellationToken">Cancels the policy work.</param>
public sealed record ToolPostContext(
    ToolExecution Execution,
    ToolExecutionResult Result,
    CancellationToken CancellationToken);

/// <summary>A settled call, for observers.</summary>
/// <param name="Execution">The call's identity and arguments.</param>
/// <param name="Result">The final outcome.</param>
public sealed record ToolResultNotice(ToolExecution Execution, ToolExecutionResult Result);

/// <summary>Context and event keys the tool capability publishes.</summary>
public static class ToolKeys
{
    /// <summary>The context key <see cref="ToolRuntime" /> is published under.</summary>
    public const string Service = "tools";

    /// <summary>
    /// Decides whether a call may run. A listener that owns the decision returns
    /// without delegating; one that only annotates must delegate.
    /// </summary>
    public static WaterfallKey<ToolExecutionContext, PreToolDecision> PreExecute { get; } =
        new("tools/pre-execute");

    /// <summary>
    /// Wraps the tool body. Listeners implement timeouts, retries, and recording by
    /// composing around the dispatch.
    /// </summary>
    public static WaterfallKey<ToolExecutionContext, ToolExecutionResult> Execute { get; } =
        new("tools/execute");

    /// <summary>Inspects a completed call and may replace its content or block it.</summary>
    public static WaterfallKey<ToolPostContext, PostToolDecision> PostExecute { get; } =
        new("tools/post-execute");

    /// <summary>One call settled.</summary>
    public static EmitKey<ToolResultNotice> Result { get; } = new("tools/result");

    /// <summary>The visible tool set changed, so the next prompt assembly differs.</summary>
    public static EmitKey<string> Change { get; } = new("tools/change");
}

/// <summary>The failure codes the tool pipeline itself produces.</summary>
public static class ToolErrorCodes
{
    /// <summary>The call was cancelled after its body started.</summary>
    public const string Aborted = "ABORTED";

    /// <summary>The call was cancelled before its body started.</summary>
    public const string AbortedBeforeDispatch = "ABORTED_BEFORE_DISPATCH";

    /// <summary>No tool of that name is visible to the caller.</summary>
    public const string UnknownTool = "UNKNOWN_TOOL";

    /// <summary>The model's arguments did not match the tool's schema.</summary>
    public const string InvalidArgs = "INVALID_ARGS";

    /// <summary>The tool returned a value its own output schema rejects.</summary>
    public const string InvalidOutput = "INVALID_TOOL_OUTPUT";

    /// <summary>Policy refused the call.</summary>
    public const string Denied = "TOOL_DENIED";

    /// <summary>The tool body threw.</summary>
    public const string Failed = "TOOL_FAILED";
}

/// <summary>
/// The tool registry and the one path every model-facing call takes.
/// </summary>
/// <remarks>
/// Registrations are layered: a global layer plus one layer per registration
/// boundary. Restrictions <b>intersect</b> down the chain and guards can only refuse,
/// so no listener order and no later registration can widen what an agent may do. A
/// scope's own registrations are exempt from restrictions, because a delegation
/// runtime registers a child's reporting tools into the child's own layer and a
/// capability filter must not strip them.
/// </remarks>
public sealed class ToolRuntime : Service
{
    private sealed class Layer
    {
        public Dictionary<string, ITool> Tools { get; } = new(StringComparer.Ordinal);
        public List<ToolRestriction> Restrictions { get; } = [];
        public List<ToolGuard> Guards { get; } = [];
    }

    private readonly Layer _global = new();
    private readonly Dictionary<ScopeKey, Layer> _scoped = [];
    private readonly object _gate = new();

    /// <param name="ctx">The mounting plugin's context.</param>
    public ToolRuntime(Context ctx) : base(ctx, ToolKeys.Service) { }

    private Layer LayerFor(ScopeKey? scope)
    {
        if (scope is null) return _global;
        lock (_gate)
        {
            if (!_scoped.TryGetValue(scope, out var layer))
            {
                layer = new Layer();
                _scoped[scope] = layer;
            }

            return layer;
        }
    }

    /// <summary>
    /// Publish a tool, globally or into the calling context's boundary.
    /// </summary>
    /// <param name="ctx">The registering context; its scope decides which layer the tool joins.</param>
    /// <param name="tool">The tool to publish.</param>
    /// <returns>A disposer that withdraws it.</returns>
    /// <exception cref="InvalidOperationException">The layer already has a tool of that name, or the schemas are unenforceable.</exception>
    public IDisposable Register(Context ctx, ITool tool)
    {
        tool.Parameters.AssertSupported();
        tool.Output.Schema.AssertSupported();
        if (tool.TimeoutMs is { } timeout && timeout <= 0)
        {
            throw new InvalidOperationException($"tool \"{tool.Name}\" declares a non-positive timeout");
        }

        var layer = LayerFor(ctx.Scope);
        lock (_gate)
        {
            if (layer.Tools.ContainsKey(tool.Name))
            {
                throw new InvalidOperationException($"tool \"{tool.Name}\" is already registered in this scope");
            }

            layer.Tools[tool.Name] = tool;
        }

        Announce();

        return ctx.Effect(new ActionDisposable(() =>
        {
            lock (_gate)
            {
                if (layer.Tools.TryGetValue(tool.Name, out var current) && ReferenceEquals(current, tool))
                {
                    layer.Tools.Remove(tool.Name);
                }
            }

            Announce();
        }));
    }

    /// <summary>
    /// Narrow which inherited tools a boundary may see.
    /// </summary>
    /// <param name="ctx">The scoped context the restriction applies to.</param>
    /// <param name="restriction">The filter.</param>
    /// <returns>A disposer that lifts it.</returns>
    /// <exception cref="InvalidOperationException">The context is unscoped, or the filter names nothing.</exception>
    public IDisposable Restrict(Context ctx, ToolRestriction restriction)
    {
        if (ctx.Scope is null)
        {
            throw new InvalidOperationException("a tool restriction needs a scoped context to apply to");
        }

        if (restriction.Allow is null && restriction.Deny is null)
        {
            throw new InvalidOperationException("a tool restriction must name something to allow or deny");
        }

        var layer = LayerFor(ctx.Scope);
        lock (_gate) layer.Restrictions.Add(restriction);
        Announce();

        return ctx.Effect(new ActionDisposable(() =>
        {
            lock (_gate) layer.Restrictions.Remove(restriction);
            Announce();
        }));
    }

    /// <summary>
    /// Add a denial that no other listener can overturn.
    /// </summary>
    /// <param name="ctx">The registering context; its scope decides where the guard applies.</param>
    /// <param name="guard">Returns a refusal reason, or null to have no opinion.</param>
    /// <returns>A disposer that removes it.</returns>
    public IDisposable Guard(Context ctx, ToolGuard guard)
    {
        var layer = LayerFor(ctx.Scope);
        lock (_gate) layer.Guards.Add(guard);

        return ctx.Effect(new ActionDisposable(() =>
        {
            lock (_gate) layer.Guards.Remove(guard);
        }));
    }

    /// <summary>
    /// The tools one boundary can call.
    /// </summary>
    /// <param name="scope">The boundary, or null for the global view.</param>
    /// <returns>The visible tools, by name.</returns>
    public IReadOnlyDictionary<string, ITool> View(ScopeKey? scope)
    {
        lock (_gate)
        {
            var restrictions = new List<ToolRestriction>();
            Layer? own = null;
            if (scope is not null && _scoped.TryGetValue(scope, out var scopedLayer))
            {
                own = scopedLayer;
                restrictions.AddRange(scopedLayer.Restrictions);
            }

            restrictions.AddRange(_global.Restrictions);

            var visible = new Dictionary<string, ITool>(StringComparer.Ordinal);
            foreach (var (name, tool) in _global.Tools)
            {
                var admitted = true;
                foreach (var restriction in restrictions)
                {
                    if (restriction.Admits(name)) continue;
                    admitted = false;
                    break;
                }

                if (admitted) visible[name] = tool;
            }

            // A boundary's own registrations shadow inherited names and are exempt from
            // restrictions: a capability filter must not strip the tools that were
            // registered specifically for this boundary.
            if (own is not null)
            {
                foreach (var (name, tool) in own.Tools) visible[name] = tool;
            }

            return visible;
        }
    }

    /// <summary>
    /// The tool schemas one boundary sends to the model.
    /// </summary>
    /// <param name="scope">The boundary, or null for the global view.</param>
    /// <returns>
    /// One schema per visible tool, sorted by name and projected to exactly what the
    /// model may see — timeouts, presenters, and concurrency metadata stay behind.
    /// </returns>
    public IReadOnlyList<ToolSchema> Schemas(ScopeKey? scope)
        => [.. View(scope)
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .Select(static entry => new ToolSchema(
                entry.Value.Name,
                entry.Value.Description,
                entry.Value.Parameters.ToWire()))];

    /// <summary>
    /// How one call may overlap with its siblings.
    /// </summary>
    /// <param name="input">The call to classify.</param>
    /// <returns>
    /// Parallel only when a visible tool says so plainly. An unknown tool, a hidden
    /// one, or a classifier that throws all yield exclusive, so uncertainty always
    /// produces a barrier rather than an overlap.
    /// </returns>
    public ToolExecutionMode ExecutionMode(ToolExecutionInput input)
    {
        if (!View(input.Scope).TryGetValue(input.Name, out var tool)) return ToolExecutionMode.Exclusive;
        try
        {
            return tool.IsConcurrencySafe(input.Arguments) ? ToolExecutionMode.Parallel : ToolExecutionMode.Exclusive;
        }
        catch (Exception error)
        {
            Ctx.Logger.Log(LogLevel.Warn, ToolKeys.Service, $"tool \"{input.Name}\" concurrency classifier failed", error);
            return ToolExecutionMode.Exclusive;
        }
    }

    /// <summary>
    /// Run one tool call through the whole pipeline.
    /// </summary>
    /// <param name="input">The call to run.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The outcome, never an exception: every failure — a refusal, a bad argument, a
    /// throwing body — comes back as a result the model can read and act on.
    /// </returns>
    public async Task<ToolExecutionResult> ExecuteAsync(
        ToolExecutionInput input,
        CancellationToken cancellationToken)
    {
        var execution = new ToolExecution(input, Guid.NewGuid());
        var context = new ToolExecutionContext(execution, cancellationToken);

        if (cancellationToken.IsCancellationRequested)
        {
            return Failure("tool call aborted before dispatch", ToolErrorCodes.AbortedBeforeDispatch);
        }

        var gate = await Ctx.WaterfallAsync(
            ToolKeys.PreExecute,
            context,
            () => Task.FromResult<PreToolDecision>(AllowDecision.Instance),
            input.Scope);

        if (gate is AskDecision ask)
        {
            gate = await ResolveApprovalAsync(execution, ask, cancellationToken);
        }

        if (gate is DenyDecision denied) return Deny(execution, denied.Reason);

        if (GuardReason(execution) is { } guarded) return Deny(execution, guarded);

        ToolExecutionResult result;
        try
        {
            result = await Ctx.WaterfallAsync(
                ToolKeys.Execute,
                context,
                () => DispatchAsync(execution, cancellationToken),
                input.Scope);
        }
        catch (OperationCanceledException)
        {
            result = Failure("tool call aborted", ToolErrorCodes.Aborted);
        }
        catch (Exception error)
        {
            result = Failure(ErrorChain.Describe(error), ToolErrorCodes.Failed);
        }

        var decision = await Ctx.WaterfallAsync(
            ToolKeys.PostExecute,
            new ToolPostContext(execution, result, cancellationToken),
            () => Task.FromResult<PostToolDecision>(new AcceptDecision()),
            input.Scope);

        result = Apply(result, decision);
        Ctx.Emit(ToolKeys.Result, new ToolResultNotice(execution, result), input.Scope);
        return result;
    }

    private async Task<PreToolDecision> ResolveApprovalAsync(
        ToolExecution execution,
        AskDecision ask,
        CancellationToken cancellationToken)
    {
        var approval = Ctx.Get<IApprovalService>(ApprovalKeys.Service);
        if (approval is null)
        {
            return new DenyDecision(
                $"{execution.Name} needs approval, but no approval channel is available in this deployment");
        }

        ApprovalOutcome outcome;
        try
        {
            outcome = await approval.RequestAsync(
                new ApprovalRequest(execution.Name, execution.CallId.Value, ask.Reason),
                cancellationToken);
        }
        catch (Exception error)
        {
            Ctx.Logger.Log(LogLevel.Warn, ToolKeys.Service, "approval request failed", error);
            outcome = ApprovalOutcome.Unavailable;
        }

        return outcome switch
        {
            ApprovalOutcome.AllowedOnce => AllowDecision.Instance,
            ApprovalOutcome.Rejected => new DenyDecision($"the user rejected the {execution.Name} call"),
            ApprovalOutcome.Cancelled => new DenyDecision($"the approval request for {execution.Name} was withdrawn"),
            _ => new DenyDecision($"approval for {execution.Name} could not be obtained"),
        };
    }

    private string? GuardReason(ToolExecution execution)
    {
        List<ToolGuard> guards;
        lock (_gate)
        {
            guards = [.. _global.Guards];
            if (execution.Input.Scope is not null && _scoped.TryGetValue(execution.Input.Scope, out var layer))
            {
                guards.AddRange(layer.Guards);
            }
        }

        foreach (var guard in guards)
        {
            string? reason;
            try
            {
                reason = guard(execution);
            }
            catch (Exception error)
            {
                // A guard that cannot answer refuses: a failure here must never widen
                // what a call is allowed to do.
                Ctx.Logger.Log(LogLevel.Warn, ToolKeys.Service, "tool guard failed", error);
                reason = $"a policy check on {execution.Name} failed";
            }

            if (reason is not null) return reason;
        }

        return null;
    }

    private async Task<ToolExecutionResult> DispatchAsync(
        ToolExecution execution,
        CancellationToken cancellationToken)
    {
        if (!View(execution.Input.Scope).TryGetValue(execution.Name, out var tool))
        {
            return Failure($"no tool named \"{execution.Name}\" is available", ToolErrorCodes.UnknownTool);
        }

        var violations = JsonSchemaValidator.Validate(tool.Parameters, execution.Arguments);
        if (violations.Count > 0)
        {
            return Failure(
                $"invalid arguments for {execution.Name}: {string.Join("; ", violations)}",
                ToolErrorCodes.InvalidArgs);
        }

        var run = new ToolRunContext(execution, cancellationToken);
        JsonValue value;
        try
        {
            value = await tool.ExecuteAsync(execution.Arguments, run);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure("tool call aborted", ToolErrorCodes.Aborted);
        }
        catch (HarnessError error)
        {
            return Failure(error.Message, error.Code);
        }
        catch (Exception error)
        {
            return Failure(ErrorChain.Describe(error), ToolErrorCodes.Failed);
        }

        var outputViolations = JsonSchemaValidator.Validate(tool.Output.Schema, value);
        if (outputViolations.Count > 0)
        {
            return Failure(
                $"{execution.Name} returned a value its own output schema rejects: {string.Join("; ", outputViolations)}",
                ToolErrorCodes.InvalidOutput);
        }

        IReadOnlyList<ContentBlock> content;
        try
        {
            content = tool.Output.Render(execution.Arguments, value);
        }
        catch (Exception error)
        {
            return Failure($"{execution.Name} could not render its result: {ErrorChain.Describe(error)}", ToolErrorCodes.Failed);
        }

        JsonValue? meta = null;
        if (tool.Output.PresentationMeta is { } project)
        {
            try
            {
                meta = project(execution.Arguments, value);
            }
            catch (Exception error)
            {
                // Presentation is never worth failing a successful call for.
                Ctx.Logger.Log(LogLevel.Warn, ToolKeys.Service, $"tool \"{execution.Name}\" meta projection failed", error);
            }
        }

        return new ToolExecutionResult(
            content,
            IsError: false,
            Value: value,
            Meta: meta,
            AdditionalContexts: run.DeferredContext.Count > 0 ? run.DeferredContext : null,
            ConcludesTurn: run.TurnConcluded);
    }

    private static ToolExecutionResult Apply(ToolExecutionResult result, PostToolDecision decision)
    {
        switch (decision)
        {
            case BlockDecision block:
            {
                var contexts = Combine(result.AdditionalContexts, block.AdditionalContexts);
                return result with
                {
                    Content = block.Feedback,
                    IsError = true,
                    Value = null,
                    Error = new ToolErrorInfo("BlockedError", ToolErrorCodes.Denied),
                    AdditionalContexts = contexts,
                    ConcludesTurn = false,
                };
            }

            case AcceptDecision accept:
            {
                var contexts = Combine(result.AdditionalContexts, accept.AdditionalContexts);
                return result with
                {
                    Content = accept.Content ?? result.Content,
                    AdditionalContexts = contexts,
                };
            }

            default:
                return result;
        }
    }

    private static IReadOnlyList<Message>? Combine(IReadOnlyList<Message>? first, IReadOnlyList<Message>? second)
    {
        if (first is null || first.Count == 0) return second is { Count: > 0 } ? second : null;
        if (second is null || second.Count == 0) return first;
        return [.. first, .. second];
    }

    private ToolExecutionResult Deny(ToolExecution execution, string reason)
    {
        Ctx.Logger.Log(LogLevel.Debug, ToolKeys.Service, $"refused {execution.Name}: {reason}");
        return Failure(reason, ToolErrorCodes.Denied);
    }

    private static ToolExecutionResult Failure(string message, string code)
        => new(
            [new TextBlock($"Error: {message}")],
            IsError: true,
            Error: new ToolErrorInfo(code == ToolErrorCodes.Denied ? "DeniedError" : "ToolError", code));

    private void Announce() => Ctx.Emit(ToolKeys.Change, ToolKeys.Service);

    /// <summary>Mount the tool capability.</summary>
    /// <returns>The plugin to hand to <see cref="Context.Plugin" />.</returns>
    public static IPlugin Plugin()
        => ServicePlugin.Create("tools", ToolKeys.Service, ctx => new ToolRuntime(ctx));
}

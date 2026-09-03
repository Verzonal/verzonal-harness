using Dsh.Cordis;
using Dsh.Llm;
using Dsh.Session;

namespace Dsh.Agent;

/// <summary>Whether an agent is working.</summary>
public enum AgentStatus
{
    /// <summary>Nothing is running; input will start a turn.</summary>
    Idle,

    /// <summary>A turn is in flight.</summary>
    Running,
}

/// <summary>Why a session began this time.</summary>
public enum SessionStartSource
{
    /// <summary>A new session at process start.</summary>
    Startup,

    /// <summary>An existing session reopened.</summary>
    Resume,

    /// <summary>History was cleared.</summary>
    Clear,

    /// <summary>History was compacted.</summary>
    Compact,
}

/// <summary>The route and ceiling one agent's requests use.</summary>
/// <param name="Provider">The registered provider route.</param>
/// <param name="Model">The provider-owned model id.</param>
/// <param name="MaxTokens">An output ceiling the caller insists on, when any.</param>
public sealed record AgentOptions(string? Provider = null, string? Model = null, int? MaxTokens = null);

/// <summary>How a cancellation should treat pending work.</summary>
/// <param name="KeepInbox">Leave queued work in place instead of discarding it.</param>
public sealed record CancelOptions(bool KeepInbox = false);

/// <summary>Whether a proposed step may run, and with what.</summary>
public abstract record PreStepDecision;

/// <summary>Refuse the step; the turn closes having spent no model call.</summary>
public sealed record RejectStep : PreStepDecision
{
    /// <summary>The shared instance.</summary>
    public static RejectStep Instance { get; } = new();
}

/// <summary>Run the step with these messages, which a listener may have rewritten.</summary>
/// <param name="Messages">What enters the step.</param>
public sealed record EnterStep(IReadOnlyList<Message> Messages) : PreStepDecision;

/// <summary>What to do about a failed model request.</summary>
public abstract record RequestErrorAction;

/// <summary>Try the request again; the loop rebuilds it from the log first.</summary>
public sealed record RetryRequest : RequestErrorAction
{
    /// <summary>The shared instance.</summary>
    public static RetryRequest Instance { get; } = new();
}

/// <summary>Let the failure end the turn.</summary>
public sealed record TerminalRequestFailure : RequestErrorAction
{
    /// <summary>The shared instance.</summary>
    public static TerminalRequestFailure Instance { get; } = new();
}

/// <summary>
/// A live agent: one session, one driver, and the handles to steer it.
/// </summary>
public interface IAgent
{
    /// <summary>The identity shared with the session it drives.</summary>
    SessionId Id { get; }

    /// <summary>The route this agent's requests use.</summary>
    AgentOptions Options { get; }

    /// <summary>The log that is this agent's source of truth.</summary>
    Session.Session Session { get; }

    /// <summary>Its pending work.</summary>
    Inbox Inbox { get; }

    /// <summary>Whether a turn is in flight.</summary>
    AgentStatus Status { get; }

    /// <summary>
    /// The agent-scoped context. Contributions made through it are local to this
    /// agent, unwind when it is disposed, and are refused afterwards.
    /// </summary>
    Context Ctx { get; }

    /// <summary>The registration boundary this agent's events and contributions carry.</summary>
    ScopeKey Scope { get; }

    /// <summary>
    /// Stop the current turn.
    /// </summary>
    /// <param name="cause">Why it is being stopped, recorded on the turn's end.</param>
    /// <param name="options">Whether queued work survives.</param>
    void Cancel(AgentCancelCause cause, CancelOptions? options = null);

    /// <summary>
    /// Wait until nothing is running, following replacement work scheduled while waiting.
    /// </summary>
    /// <returns>A task completing when the agent is idle.</returns>
    Task WhenIdleAsync();

    /// <summary>
    /// Run work that needs the agent to itself, such as compaction.
    /// </summary>
    /// <typeparam name="T">What the work produces.</typeparam>
    /// <param name="job">The work, which must observe its cancellation token.</param>
    /// <returns>The work's result.</returns>
    /// <exception cref="InvalidOperationException">A turn is already running.</exception>
    Task<T> RunMaintenanceAsync<T>(Func<CancellationToken, Task<T>> job);

    /// <summary>
    /// Put a message into the inbox.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="target">Which boundary it waits for.</param>
    /// <param name="wakeup">Whether it should start a turn if none is running.</param>
    void Send(Message message, InboxTarget target, bool wakeup);

    /// <summary>
    /// Queue a prompt that opens its own turn.
    /// </summary>
    /// <param name="message">The prompt.</param>
    void Followup(Message message);

    /// <summary>
    /// Add input to the running turn's next step, waking the agent if it is idle.
    /// </summary>
    /// <param name="message">The steering input.</param>
    void Steer(Message message);

    /// <summary>
    /// Add context the model should see, without waking an idle agent. It waits until
    /// something else starts a turn.
    /// </summary>
    /// <param name="message">The context.</param>
    void Inject(Message message);
}

/// <summary>One agent, for an observe-only notification.</summary>
/// <param name="Agent">The agent the notice is about.</param>
public sealed record AgentNotice(IAgent Agent);

/// <summary>An agent's status transition.</summary>
/// <param name="Agent">The agent.</param>
/// <param name="Status">Its new status.</param>
public sealed record AgentStatusNotice(IAgent Agent, AgentStatus Status);

/// <summary>An agent's session beginning.</summary>
/// <param name="Agent">The agent.</param>
/// <param name="Source">Why the session began this time.</param>
public sealed record AgentSessionStart(IAgent Agent, SessionStartSource Source);

/// <summary>One message entering, leaving, or being claimed from an inbox.</summary>
/// <param name="Agent">The agent.</param>
/// <param name="Message">The message.</param>
/// <param name="Turn">The claiming turn, on a claim.</param>
public sealed record AgentInboxNotice(IAgent Agent, Message Message, int? Turn = null);

/// <summary>A failure reported at its live boundary.</summary>
/// <param name="Agent">The agent.</param>
/// <param name="Turn">Where it happened.</param>
/// <param name="Step">Where it happened.</param>
/// <param name="Error">What went wrong.</param>
public sealed record AgentErrorNotice(IAgent Agent, int Turn, int Step, Exception Error);

/// <summary>A step being proposed.</summary>
/// <param name="Agent">The agent.</param>
/// <param name="Messages">What the loop claimed for this step.</param>
/// <param name="Turn">The turn.</param>
/// <param name="Step">The step.</param>
/// <param name="CancellationToken">Cancels the decision.</param>
public sealed record PreStepPayload(
    IAgent Agent,
    IReadOnlyList<Message> Messages,
    int Turn,
    int Step,
    CancellationToken CancellationToken);

/// <summary>A request being configured.</summary>
/// <param name="Agent">The agent.</param>
/// <param name="Turn">The turn.</param>
/// <param name="Step">The step.</param>
/// <param name="CancellationToken">Cancels the decision.</param>
public sealed record RequestPayload(IAgent Agent, int Turn, int Step, CancellationToken CancellationToken);

/// <summary>A failed request attempt.</summary>
/// <param name="Agent">The agent.</param>
/// <param name="Turn">The turn.</param>
/// <param name="Step">The step.</param>
/// <param name="Provider">The route that failed.</param>
/// <param name="Failure">What went wrong.</param>
/// <param name="RetryPolicy">What the provider wants done about it.</param>
/// <param name="Attempt">Which attempt this was, counting the first as one.</param>
/// <param name="CancellationToken">Cancels the decision.</param>
public sealed record RequestErrorPayload(
    IAgent Agent,
    int Turn,
    int Step,
    string Provider,
    LlmFailure Failure,
    ResolvedRetryPolicy? RetryPolicy,
    int Attempt,
    CancellationToken CancellationToken);

/// <summary>A turn about to close.</summary>
/// <param name="Agent">The agent.</param>
/// <param name="Turn">The turn.</param>
/// <param name="CancellationToken">Cancels the objection window.</param>
public sealed record TurnStoppingPayload(IAgent Agent, int Turn, CancellationToken CancellationToken);

/// <summary>Context and event keys the agent capability publishes.</summary>
public static class AgentKeys
{
    /// <summary>The context key <see cref="AgentRegistry" /> is published under.</summary>
    public const string Service = "agents";

    /// <summary>The context key the driver factory is published under.</summary>
    public const string LoopService = "agentLoop";

    /// <summary>An agent has been published. A throw here vetoes publication.</summary>
    public static EmitKey<AgentNotice> Created { get; } = new("agent/created");

    /// <summary>An agent has been torn down.</summary>
    public static EmitKey<AgentNotice> Disposed { get; } = new("agent/disposed");

    /// <summary>An agent moved between idle and running.</summary>
    public static EmitKey<AgentStatusNotice> Status { get; } = new("agent/status");

    /// <summary>An agent's session began; the first point at which startup work can run.</summary>
    public static EmitKey<AgentSessionStart> SessionStart { get; } = new("agent/session-start");

    /// <summary>A message joined an inbox.</summary>
    public static EmitKey<AgentInboxNotice> InboxInserted { get; } = new("agent/inbox/inserted");

    /// <summary>A turn took a message to run it.</summary>
    public static EmitKey<AgentInboxNotice> InboxClaimed { get; } = new("agent/inbox/claimed");

    /// <summary>A message was dropped without being run.</summary>
    public static EmitKey<AgentInboxNotice> InboxDiscarded { get; } = new("agent/inbox/discarded");

    /// <summary>A step or turn failed.</summary>
    public static EmitKey<AgentErrorNotice> Error { get; } = new("agent/error");

    /// <summary>
    /// Decides what the model sees. A listener may rewrite the entering messages or
    /// refuse the step outright.
    /// </summary>
    public static WaterfallKey<PreStepPayload, PreStepDecision> PreStep { get; } = new("agent/pre-step");

    /// <summary>
    /// Decides how the next request is configured. Delegating yields the configuration
    /// the loop would have used; returning a different one switches the route.
    /// </summary>
    public static WaterfallKey<RequestPayload, LlmCallConfig> Request { get; } = new("agent/request");

    /// <summary>
    /// Handles one failed request before the loop retries or gives up. A listener that
    /// owns recovery returns a retry without delegating.
    /// </summary>
    public static WaterfallKey<RequestErrorPayload, RequestErrorAction> RequestError { get; } =
        new("agent/request-error");

    /// <summary>
    /// The turn is about to close because the model owes nothing further.
    /// </summary>
    /// <remarks>
    /// An objector steers the agent rather than returning a veto, and the loop
    /// re-reads its inbox afterwards. Because the decision is data, listener order
    /// cannot change the outcome.
    /// </remarks>
    public static SerialKey<TurnStoppingPayload> TurnStopping { get; } = new("agent/turn-stopping");
}

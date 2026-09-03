using System.Text.Json.Serialization;
using Dsh.Llm;

namespace Dsh.Session;

/// <summary>Why a driver's active work was cancelled.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(UserCancel), "user")]
[JsonDerivedType(typeof(ParentCancel), "parent")]
[JsonDerivedType(typeof(HookCancel), "hook")]
[JsonDerivedType(typeof(DisposedCancel), "disposed")]
[JsonDerivedType(typeof(LegacyCancel), "legacy")]
public abstract record AgentCancelCause;

/// <summary>A person stopped the turn.</summary>
public sealed record UserCancel : AgentCancelCause
{
    /// <summary>The shared instance.</summary>
    public static UserCancel Instance { get; } = new();
}

/// <summary>A parent agent stopped its child.</summary>
public sealed record ParentCancel : AgentCancelCause
{
    /// <summary>The shared instance.</summary>
    public static ParentCancel Instance { get; } = new();
}

/// <summary>A policy listener stopped the turn.</summary>
/// <param name="Reason">What the listener objected to.</param>
public sealed record HookCancel(string Reason) : AgentCancelCause;

/// <summary>The agent was torn down.</summary>
public sealed record DisposedCancel : AgentCancelCause
{
    /// <summary>The shared instance.</summary>
    public static DisposedCancel Instance { get; } = new();
}

/// <summary>An imported record whose original cancellation cause was not preserved.</summary>
public sealed record LegacyCancel : AgentCancelCause
{
    /// <summary>The shared instance.</summary>
    public static LegacyCancel Instance { get; } = new();
}

/// <summary>Why a turn ended. Every turn closes with exactly one of these.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CompletedTurnEnd), "completed")]
[JsonDerivedType(typeof(AbortedTurnEnd), "aborted")]
[JsonDerivedType(typeof(BlockedTurnEnd), "blocked")]
[JsonDerivedType(typeof(ErrorTurnEnd), "error")]
[JsonDerivedType(typeof(MaxTokensTurnEnd), "max-tokens")]
[JsonDerivedType(typeof(InterruptedTurnEnd), "interrupted")]
public abstract record TurnEndReason;

/// <summary>The model owed nothing further and the turn closed normally.</summary>
public sealed record CompletedTurnEnd : TurnEndReason
{
    /// <summary>The shared instance.</summary>
    public static CompletedTurnEnd Instance { get; } = new();
}

/// <summary>A cancellation interrupted the live turn.</summary>
/// <param name="Reason">What caused the cancellation.</param>
public sealed record AbortedTurnEnd(AgentCancelCause Reason) : TurnEndReason;

/// <summary>A policy listener rejected the step, so the turn spent no model call.</summary>
public sealed record BlockedTurnEnd : TurnEndReason
{
    /// <summary>The shared instance.</summary>
    public static BlockedTurnEnd Instance { get; } = new();
}

/// <summary>The turn failed.</summary>
/// <param name="Error">The structured failure, keeping a provider failure's facts verbatim.</param>
public sealed record ErrorTurnEnd(LlmFailure Error) : TurnEndReason;

/// <summary>
/// At least one step hit the output-token ceiling. Sticky: a later step completing
/// normally does not downgrade the turn's outcome.
/// </summary>
public sealed record MaxTokensTurnEnd : TurnEndReason
{
    /// <summary>The shared instance.</summary>
    public static MaxTokensTurnEnd Instance { get; } = new();
}

/// <summary>
/// A crash left this turn open and a reader closed it. The loop never writes this;
/// the events recorded before the crash stay intact.
/// </summary>
public sealed record InterruptedTurnEnd : TurnEndReason
{
    /// <summary>The shared instance.</summary>
    public static InterruptedTurnEnd Instance { get; } = new();
}

/// <summary>Lifecycle state of one todo entry.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TodoStatus>))]
public enum TodoStatus
{
    /// <summary>Not started.</summary>
    Pending,

    /// <summary>Being worked on now.</summary>
    InProgress,

    /// <summary>Finished.</summary>
    Completed,
}

/// <summary>
/// One entry of the agent's checklist. Deliberately minimal and identity-free: the
/// list is replaced wholesale on every write, so entries need no stable id.
/// </summary>
/// <param name="Content">What the task is, as a short imperative line.</param>
/// <param name="Status">Where it stands.</param>
public sealed record TodoItem(string Content, TodoStatus Status);

/// <summary>Why a request header was recorded.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RequestHeaderReason>))]
public enum RequestHeaderReason
{
    /// <summary>The log's first header — a new conversation.</summary>
    Initial,

    /// <summary>A new loop instance's first request over a log that already had one.</summary>
    Resume,

    /// <summary>A later request used a different header.</summary>
    Change,
}

/// <summary>
/// The request state that lives outside derived history: how to call the model, the
/// rendered system prompt, and the assembled tool schemas. The latest snapshot plus
/// the derived messages reconstruct a request exactly.
/// </summary>
/// <param name="Config">The call configuration.</param>
/// <param name="AdapterDefaults">Which configuration fields the adapter supplied.</param>
/// <param name="System">The rendered system prompt; absent when empty.</param>
/// <param name="Tools">The assembled tool schemas; absent when none.</param>
public sealed record EpochHeader(
    LlmCallConfig Config,
    LlmCallConfigAdapterDefaults? AdapterDefaults = null,
    string? System = null,
    IReadOnlyList<ToolSchema>? Tools = null);

/// <summary>Opens a turn.</summary>
/// <param name="Turn">The turn number, counting from one.</param>
public sealed record TurnStartData(int Turn);

/// <summary>Closes a turn.</summary>
/// <param name="Turn">The turn being closed.</param>
/// <param name="Reason">Why it ended.</param>
public sealed record TurnEndData(int Turn, TurnEndReason Reason);

/// <summary>Opens one model call and the tools it requests.</summary>
/// <param name="Turn">The enclosing turn.</param>
/// <param name="Step">The step number within that turn, counting from one.</param>
public sealed record StepStartData(int Turn, int Step);

/// <summary>Closes one step.</summary>
/// <param name="Turn">The enclosing turn.</param>
/// <param name="Step">The step being closed.</param>
public sealed record StepEndData(int Turn, int Step);

/// <summary>One raw stream chunk, kept for token-level replay fidelity.</summary>
/// <param name="Turn">The enclosing turn.</param>
/// <param name="Step">The enclosing step.</param>
/// <param name="Chunk">The chunk exactly as the adapter produced it.</param>
public sealed record AssistantChunkData(int Turn, int Step, StreamChunk Chunk);

/// <summary>The assembled assistant message for one step.</summary>
/// <param name="Turn">The enclosing turn.</param>
/// <param name="Step">The enclosing step.</param>
/// <param name="Message">The message derived history uses.</param>
/// <param name="Usage">Token accounting, when the adapter reported any.</param>
/// <param name="Interrupted">
/// True when this records the prefix delivered before a cancellation; undispatched
/// tool calls are absent from such a message.
/// </param>
public sealed record AssistantMessageData(
    int Turn,
    int Step,
    Message Message,
    TokenUsage? Usage = null,
    bool Interrupted = false);

/// <summary>One tool invocation the model requested.</summary>
/// <param name="Turn">The enclosing turn.</param>
/// <param name="Step">The enclosing step.</param>
/// <param name="CallId">Pairs this call with its result.</param>
/// <param name="Name">The tool name as the model wrote it.</param>
/// <param name="Arguments">The raw JSON string the model produced, unparsed.</param>
public sealed record ToolCallData(int Turn, int Step, CallId CallId, string Name, string Arguments);

/// <summary>The internal identity of a tool failure, kept out of the model's view.</summary>
/// <param name="Name">The failure's type name.</param>
/// <param name="Code">Its machine-readable code.</param>
public sealed record ToolErrorInfo(string Name, string Code);

/// <summary>One completed tool call's model-facing outcome.</summary>
/// <param name="Turn">The enclosing turn.</param>
/// <param name="Step">The enclosing step.</param>
/// <param name="Message">The tool-result message derived history uses.</param>
/// <param name="Error">The internal failure identity, when the call failed.</param>
/// <param name="Meta">
/// The producing tool's private presentation payload, opaque to the core but stored
/// losslessly so a replay reproduces the same card.
/// </param>
public sealed record ToolResultData(
    int Turn,
    int Step,
    Message Message,
    ToolErrorInfo? Error = null,
    JsonValue? Meta = null);

/// <summary>A whole-list checklist snapshot; the latest write wins on replay.</summary>
/// <param name="Todos">The complete list, replacing any previous one.</param>
public sealed record TodoWriteData(IReadOnlyList<TodoItem> Todos);

/// <summary>The full header for the next request, recorded before it is dispatched.</summary>
/// <param name="Header">The header snapshot.</param>
/// <param name="Reason">Why it was recorded.</param>
public sealed record RequestHeaderData(EpochHeader Header, RequestHeaderReason Reason);

/// <summary>Route metadata for the next request, recorded only when it changes.</summary>
/// <param name="Provider">The registered provider route.</param>
/// <param name="Model">The provider-owned model id.</param>
/// <param name="ContextWindow">The route's capacity, when advertised.</param>
public sealed record RequestContextData(string Provider, string Model, int? ContextWindow = null);

/// <summary>
/// Marks the end of a constructor seed: every event before it came from a resume,
/// fork, or replay rather than from this lifecycle. The payload is empty — position
/// and time carry the meaning.
/// </summary>
public sealed record SessionEndSeedData
{
    /// <summary>The shared instance.</summary>
    public static SessionEndSeedData Instance { get; } = new();
}

/// <summary>
/// The core durable vocabulary. Every entry is registered on first access to this
/// class, so a reader can interpret a log written by any build that shares this core.
/// </summary>
public static class SessionEvents
{
    /// <summary>Opens a turn before any input is claimed.</summary>
    public static SessionEventType<TurnStartData> TurnStart { get; } =
        SessionEventRegistry.Register<TurnStartData>("turn/start");

    /// <summary>Closes a turn with the reason it ended.</summary>
    public static SessionEventType<TurnEndData> TurnEnd { get; } =
        SessionEventRegistry.Register<TurnEndData>("turn/end");

    /// <summary>Opens one model call plus the tools it requests.</summary>
    public static SessionEventType<StepStartData> StepStart { get; } =
        SessionEventRegistry.Register<StepStartData>("step/start");

    /// <summary>Closes one step.</summary>
    public static SessionEventType<StepEndData> StepEnd { get; } =
        SessionEventRegistry.Register<StepEndData>("step/end");

    /// <summary>
    /// A user-role message entering the model's view: a human prompt, injected
    /// context, or a tool result. The message is the payload.
    /// </summary>
    public static SessionEventType<Message> UserMessage { get; } =
        SessionEventRegistry.Register<Message>("user/message", surfaceEligible: true);

    /// <summary>One raw stream chunk.</summary>
    public static SessionEventType<AssistantChunkData> AssistantChunk { get; } =
        SessionEventRegistry.Register<AssistantChunkData>("assistant/chunk");

    /// <summary>The assembled assistant message for one step.</summary>
    public static SessionEventType<AssistantMessageData> AssistantMessage { get; } =
        SessionEventRegistry.Register<AssistantMessageData>("assistant/message", surfaceEligible: true);

    /// <summary>One tool invocation the model requested.</summary>
    public static SessionEventType<ToolCallData> ToolCall { get; } =
        SessionEventRegistry.Register<ToolCallData>("tool/call");

    /// <summary>One completed tool call's outcome.</summary>
    public static SessionEventType<ToolResultData> ToolResult { get; } =
        SessionEventRegistry.Register<ToolResultData>("tool/result", surfaceEligible: true);

    /// <summary>A whole-list checklist snapshot.</summary>
    public static SessionEventType<TodoWriteData> TodoWrite { get; } =
        SessionEventRegistry.Register<TodoWriteData>("todo/write");

    /// <summary>The full header for the next request.</summary>
    public static SessionEventType<RequestHeaderData> RequestHeader { get; } =
        SessionEventRegistry.Register<RequestHeaderData>("request/header");

    /// <summary>Route metadata for the next request.</summary>
    public static SessionEventType<RequestContextData> RequestContext { get; } =
        SessionEventRegistry.Register<RequestContextData>("request/context");

    /// <summary>The boundary between seeded history and this lifecycle's own work.</summary>
    public static SessionEventType<SessionEndSeedData> EndSeed { get; } =
        SessionEventRegistry.Register<SessionEndSeedData>("session/end-seed");

    /// <summary>
    /// Force the core vocabulary to be registered.
    /// </summary>
    /// <remarks>
    /// Reading a log touches event names before any producer has run, so a reader
    /// calls this first rather than relying on a producer's static initializer.
    /// </remarks>
    public static void EnsureRegistered()
    {
        _ = TurnStart;
        _ = TurnEnd;
        _ = StepStart;
        _ = StepEnd;
        _ = UserMessage;
        _ = AssistantChunk;
        _ = AssistantMessage;
        _ = ToolCall;
        _ = ToolResult;
        _ = TodoWrite;
        _ = RequestHeader;
        _ = RequestContext;
        _ = EndSeed;
    }
}

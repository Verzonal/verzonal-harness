using Dsh.Llm;
using Dsh.Session;

namespace Dsh.Tools;

/// <summary>
/// What a tool promises about its result, and how that result becomes content.
/// </summary>
/// <remarks>
/// A tool body returns a canonical JSON value, never content. The registry validates
/// that value against <paramref name="Schema" /> and then renders it, which is what
/// makes a result reproducible: the same value renders the same way on a live turn
/// and on a replay years later.
/// </remarks>
/// <param name="Schema">The shape every successful value must have.</param>
/// <param name="Render">Projects validated arguments and value into model-facing content.</param>
/// <param name="PresentationMeta">
/// Projects the tool's private UI payload, stored on the result event. Computed only
/// for a call the model made directly.
/// </param>
public sealed record ToolOutput(
    JsonSchemaNode Schema,
    Func<JsonValue, JsonValue, IReadOnlyList<ContentBlock>> Render,
    Func<JsonValue, JsonValue, JsonValue>? PresentationMeta = null);

/// <summary>
/// What the model can call. A tool declares the arguments it takes, the value it
/// returns, and how both should look in a UI.
/// </summary>
public interface ITool
{
    /// <summary>The name the model calls.</summary>
    string Name { get; }

    /// <summary>What the tool does, written for the model.</summary>
    string Description { get; }

    /// <summary>The arguments it accepts.</summary>
    JsonSchemaNode Parameters { get; }

    /// <summary>What it returns and how that renders.</summary>
    ToolOutput Output { get; }

    /// <summary>
    /// A cooperative deadline in milliseconds, or null for none. Never sent to the
    /// model; declaring one asserts the body observes its cancellation token.
    /// </summary>
    int? TimeoutMs { get; }

    /// <summary>
    /// Whether this call may run alongside its siblings.
    /// </summary>
    /// <param name="args">The parsed arguments.</param>
    /// <returns>
    /// True only when the call is safe to overlap. Anything else — including a throw —
    /// is treated as exclusive, so a tool that cannot answer gets a barrier.
    /// </returns>
    bool IsConcurrencySafe(JsonValue args);

    /// <summary>
    /// How the pending call should look.
    /// </summary>
    /// <param name="args">The parsed arguments.</param>
    /// <returns>The render intent, or null to fall back to a generic card.</returns>
    ToolCallView? PresentCall(JsonValue args);

    /// <summary>
    /// How the completed call should look.
    /// </summary>
    /// <param name="args">The parsed arguments.</param>
    /// <param name="result">The durable outcome.</param>
    /// <returns>The render intent, or null to keep the pending card and show raw content.</returns>
    ToolResultView? PresentResult(JsonValue args, ToolResult result);

    /// <summary>
    /// Run one accepted call.
    /// </summary>
    /// <param name="args">The validated, parsed arguments.</param>
    /// <param name="exec">Execution identity, cancellation, and the deferral hooks.</param>
    /// <returns>The canonical value declared by <see cref="ToolOutput.Schema" />.</returns>
    Task<JsonValue> ExecuteAsync(JsonValue args, ToolRunContext exec);
}

/// <summary>A tool with sensible defaults for everything but its body.</summary>
public abstract class ToolBase : ITool
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    public abstract JsonSchemaNode Parameters { get; }

    /// <inheritdoc />
    public abstract ToolOutput Output { get; }

    /// <inheritdoc />
    public virtual int? TimeoutMs => null;

    /// <inheritdoc />
    public virtual bool IsConcurrencySafe(JsonValue args) => false;

    /// <inheritdoc />
    public virtual ToolCallView? PresentCall(JsonValue args) => null;

    /// <inheritdoc />
    public virtual ToolResultView? PresentResult(JsonValue args, ToolResult result) => null;

    /// <inheritdoc />
    public abstract Task<JsonValue> ExecuteAsync(JsonValue args, ToolRunContext exec);

    /// <summary>
    /// Read one string argument.
    /// </summary>
    /// <param name="args">The parsed arguments.</param>
    /// <param name="name">The property to read.</param>
    /// <returns>The string, or null when absent or another type.</returns>
    protected static string? StringArg(JsonValue args, string name)
        => (args as JsonObject)?.Get(name) is JsonString text ? text.Value : null;

    /// <summary>
    /// Read one number argument.
    /// </summary>
    /// <param name="args">The parsed arguments.</param>
    /// <param name="name">The property to read.</param>
    /// <returns>The number, or null when absent or another type.</returns>
    protected static double? NumberArg(JsonValue args, string name)
        => (args as JsonObject)?.Get(name) is JsonNumber number ? number.Value : null;

    /// <summary>
    /// Read one boolean argument.
    /// </summary>
    /// <param name="args">The parsed arguments.</param>
    /// <param name="name">The property to read.</param>
    /// <returns>The boolean, or null when absent or another type.</returns>
    protected static bool? BoolArg(JsonValue args, string name)
        => (args as JsonObject)?.Get(name) is JsonBool flag ? flag.Value : null;
}

/// <summary>What a caller says about one tool call before the registry runs it.</summary>
/// <param name="CallId">The model's id for this call.</param>
/// <param name="Name">The tool being called.</param>
/// <param name="Arguments">The parsed arguments, or a string when the model's JSON was malformed.</param>
/// <param name="Scope">The registration boundary the call runs under.</param>
public sealed record ToolExecutionInput(
    CallId CallId,
    string Name,
    JsonValue Arguments,
    Cordis.ScopeKey? Scope = null);

/// <summary>
/// One tool call in flight: what was asked for, plus the identity the pipeline
/// stages use to correlate their observations.
/// </summary>
/// <param name="Input">What the caller asked for.</param>
/// <param name="Token">Registry-minted identity for this execution.</param>
public sealed record ToolExecution(ToolExecutionInput Input, Guid Token)
{
    /// <summary>The model's id for this call.</summary>
    public CallId CallId => Input.CallId;

    /// <summary>The tool being called.</summary>
    public string Name => Input.Name;

    /// <summary>The parsed arguments.</summary>
    public JsonValue Arguments => Input.Arguments;
}

/// <summary>
/// What a running tool body is given: its identity, its cancellation, and the two
/// ways it can affect the turn beyond returning a value.
/// </summary>
public sealed class ToolRunContext
{
    private readonly List<Message> _deferred = [];

    /// <param name="execution">The call's identity.</param>
    /// <param name="cancellationToken">Cancels the body.</param>
    public ToolRunContext(ToolExecution execution, CancellationToken cancellationToken)
    {
        Execution = execution;
        CancellationToken = cancellationToken;
    }

    /// <summary>The call's identity.</summary>
    public ToolExecution Execution { get; }

    /// <summary>Cancels the body; a tool that declares a timeout must observe it.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Whether the tool asked the loop to stop after this result commits.</summary>
    public bool TurnConcluded { get; private set; }

    /// <summary>Context the tool wants the model to see on the next step.</summary>
    public IReadOnlyList<Message> DeferredContext => _deferred;

    /// <summary>
    /// Add a message the model should see on the next step, alongside this result.
    /// </summary>
    /// <param name="context">The message to stage.</param>
    public void DeferContext(Message context) => _deferred.Add(context);

    /// <summary>
    /// Ask the loop to stop once this result has committed, rather than taking
    /// another step. Used by a tool whose whole point is to end the turn.
    /// </summary>
    public void ConcludeTurn() => TurnConcluded = true;
}

/// <summary>What a tool call produced.</summary>
/// <param name="Content">The model-facing content.</param>
/// <param name="IsError">Whether the call failed.</param>
/// <param name="Value">The canonical value, kept out of the durable log.</param>
/// <param name="Error">The internal failure identity, on a failure.</param>
/// <param name="Meta">The tool's private presentation payload.</param>
/// <param name="AdditionalContexts">Messages to stage for the next step.</param>
/// <param name="ConcludesTurn">Whether the loop should stop after committing this.</param>
public sealed record ToolExecutionResult(
    IReadOnlyList<ContentBlock> Content,
    bool IsError,
    JsonValue? Value = null,
    ToolErrorInfo? Error = null,
    JsonValue? Meta = null,
    IReadOnlyList<Message>? AdditionalContexts = null,
    bool ConcludesTurn = false);

/// <summary>Whether a call may proceed.</summary>
public abstract record PreToolDecision;

/// <summary>Let the call run.</summary>
public sealed record AllowDecision : PreToolDecision
{
    /// <summary>The shared instance.</summary>
    public static AllowDecision Instance { get; } = new();
}

/// <summary>Refuse the call.</summary>
/// <param name="Reason">What the model is told, which is also what it can act on.</param>
public sealed record DenyDecision(string Reason) : PreToolDecision;

/// <summary>Put the call to a person before running it.</summary>
/// <param name="Reason">Why approval is being asked for.</param>
public sealed record AskDecision(string? Reason = null) : PreToolDecision;

/// <summary>What to do with a completed call's result.</summary>
public abstract record PostToolDecision;

/// <summary>Keep the result, optionally replacing its content.</summary>
/// <param name="Content">Replacement content, or null to keep what the tool produced.</param>
/// <param name="AdditionalContexts">Extra messages to stage for the next step.</param>
public sealed record AcceptDecision(
    IReadOnlyList<ContentBlock>? Content = null,
    IReadOnlyList<Message>? AdditionalContexts = null) : PostToolDecision;

/// <summary>Turn the result into a failure carrying feedback for the model.</summary>
/// <param name="Feedback">What the model is told instead of the result.</param>
/// <param name="AdditionalContexts">Extra messages to stage for the next step.</param>
public sealed record BlockDecision(
    IReadOnlyList<ContentBlock> Feedback,
    IReadOnlyList<Message>? AdditionalContexts = null) : PostToolDecision;

/// <summary>How one call may overlap with its siblings.</summary>
public enum ToolExecutionMode
{
    /// <summary>Runs alone: the calls before it finish first, and none start until it ends.</summary>
    Exclusive,

    /// <summary>May run alongside other parallel calls.</summary>
    Parallel,
}

/// <summary>A denial that cannot be overturned by a later listener.</summary>
/// <param name="Execution">The call being judged.</param>
/// <returns>The reason to refuse, or null to have no opinion.</returns>
public delegate string? ToolGuard(ToolExecution Execution);

/// <summary>Which tools a scope may see.</summary>
/// <param name="Allow">When set, only these names remain visible.</param>
/// <param name="Deny">These names are removed.</param>
public sealed record ToolRestriction(IReadOnlyList<string>? Allow = null, IReadOnlyList<string>? Deny = null)
{
    /// <summary>
    /// Whether this restriction lets a name through.
    /// </summary>
    /// <param name="name">The tool name.</param>
    /// <returns>True when the name survives this filter.</returns>
    public bool Admits(string name)
    {
        if (Deny is not null && Deny.Contains(name, StringComparer.Ordinal)) return false;
        if (Allow is not null && !Allow.Contains(name, StringComparer.Ordinal)) return false;
        return true;
    }
}

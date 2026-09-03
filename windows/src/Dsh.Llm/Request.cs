namespace Dsh.Llm;

/// <summary>
/// One tool as the model sees it. Deliberately only these three fields: everything
/// else a tool declares — timeouts, concurrency, presentation — stays out of the
/// model's view.
/// </summary>
/// <param name="Name">The name the model calls.</param>
/// <param name="Description">What the tool does, written for the model.</param>
/// <param name="Parameters">A JSON Schema object describing the arguments.</param>
public sealed record ToolSchema(string Name, string Description, IReadOnlyDictionary<string, object?> Parameters);

/// <summary>
/// The part of a request that describes <em>how</em> to call the model, separate
/// from what to say. This is what the session log records as the request header, so
/// a later run can prove it would build the same call.
/// </summary>
/// <param name="Provider">The registered provider route.</param>
/// <param name="Model">The provider-owned model id.</param>
/// <param name="ReasoningEffort">The thinking level, when the route offers a choice.</param>
/// <param name="Temperature">Sampling temperature, when set.</param>
/// <param name="MaxTokens">Output-token ceiling, when set.</param>
/// <param name="Stop">Stop sequences, when set.</param>
public sealed record LlmCallConfig(
    string Provider,
    string Model,
    ReasoningEffortId? ReasoningEffort = null,
    double? Temperature = null,
    int? MaxTokens = null,
    IReadOnlyList<string>? Stop = null)
{
    /// <summary>
    /// Compare two configurations field by field, including stop sequences in order.
    /// </summary>
    /// <param name="other">The configuration to compare against.</param>
    /// <returns>True when every field matches.</returns>
    public bool Matches(LlmCallConfig? other)
    {
        if (other is null) return false;
        if (!string.Equals(Provider, other.Provider, StringComparison.Ordinal)) return false;
        if (!string.Equals(Model, other.Model, StringComparison.Ordinal)) return false;
        if (!Nullable.Equals(ReasoningEffort, other.ReasoningEffort)) return false;
        if (!Nullable.Equals(Temperature, other.Temperature)) return false;
        if (!Nullable.Equals(MaxTokens, other.MaxTokens)) return false;
        if (Stop is null != (other.Stop is null)) return false;
        if (Stop is null || other.Stop is null) return true;
        return Stop.SequenceEqual(other.Stop, StringComparer.Ordinal);
    }
}

/// <summary>
/// Which configuration fields the adapter supplied rather than the caller. A later
/// request drops these before proposing, so switching routes rematerializes the new
/// route's own defaults instead of inheriting the old one's.
/// </summary>
/// <param name="ReasoningEffort">True when the adapter chose the thinking level.</param>
/// <param name="MaxTokens">True when the adapter chose the output ceiling.</param>
public sealed record LlmCallConfigAdapterDefaults(bool ReasoningEffort = false, bool MaxTokens = false)
{
    /// <summary>Whether the adapter supplied nothing, which is the common case.</summary>
    public bool IsEmpty => !ReasoningEffort && !MaxTokens;
}

/// <summary>Why an auxiliary request was made, when it is not the conversation's own turn.</summary>
public enum RequestPurpose
{
    /// <summary>The conversation's own model call.</summary>
    Conversation,

    /// <summary>Summarizing older history to relieve context pressure.</summary>
    Compaction,

    /// <summary>Naming the session from its first prompt.</summary>
    SessionTitle,
}

/// <summary>
/// One complete model request. Built fresh for each step from the session log, never
/// mutated: the loop's invariant is that this can be rebuilt exactly from the log.
/// </summary>
/// <param name="Config">How to call the model.</param>
/// <param name="Messages">The derived model history.</param>
/// <param name="System">The rendered system prompt, absent when empty.</param>
/// <param name="Tools">The assembled tool schemas, absent when none.</param>
/// <param name="SessionId">The session the request belongs to, for provider-side correlation.</param>
/// <param name="Purpose">Why the request is being made.</param>
public sealed record GenerateOptions(
    LlmCallConfig Config,
    IReadOnlyList<Message> Messages,
    string? System = null,
    IReadOnlyList<ToolSchema>? Tools = null,
    SessionId? SessionId = null,
    RequestPurpose Purpose = RequestPurpose.Conversation)
{
    /// <summary>The registered provider route this request goes to.</summary>
    public string Provider => Config.Provider;

    /// <summary>The provider-owned model id this request goes to.</summary>
    public string Model => Config.Model;
}

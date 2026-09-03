using System.Text.Json.Serialization;

namespace Dsh.Llm;

/// <summary>
/// Token accounting for one request. The counts are <b>disjoint</b>: an adapter
/// that receives a total including cache hits subtracts them before reporting, so
/// summing the fields never double-counts.
/// </summary>
/// <param name="InputTokens">Uncached input tokens.</param>
/// <param name="OutputTokens">Generated tokens.</param>
/// <param name="CacheReadTokens">Input tokens served from the provider's cache.</param>
/// <param name="CacheWriteTokens">Input tokens written into the provider's cache.</param>
/// <param name="ReasoningTokens">Output tokens spent on thinking, when reported separately.</param>
public sealed record TokenUsage(
    int InputTokens,
    int OutputTokens,
    int? CacheReadTokens = null,
    int? CacheWriteTokens = null,
    int? ReasoningTokens = null)
{
    /// <summary>Every input token the request was billed for, cached and uncached.</summary>
    public int TotalInputTokens => InputTokens + (CacheReadTokens ?? 0) + (CacheWriteTokens ?? 0);
}

/// <summary>
/// A provider failure in harness-neutral terms, so policy can act on it without
/// knowing which provider produced it.
/// </summary>
/// <param name="Message">Human-readable description; never contains a credential.</param>
/// <param name="Code">Machine-readable classification such as <c>RATE_LIMIT</c> or <c>SERVER</c>.</param>
/// <param name="Status">The HTTP status, when the failure came from a response.</param>
/// <param name="ProviderRetryAfterMs">A provider-stated wait, when one was sent.</param>
/// <param name="RequestId">The provider's request id, for matching against its logs.</param>
public sealed record LlmFailure(
    string Message,
    string Code,
    int? Status = null,
    double? ProviderRetryAfterMs = null,
    ProviderRequestId? RequestId = null);

/// <summary>
/// Why a model stream ended. A terminal chunk always carries one of these, so a
/// consumer never has to infer the outcome from what did or did not arrive.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(StopFinish), "stop")]
[JsonDerivedType(typeof(ToolCallsFinish), "tool-calls")]
[JsonDerivedType(typeof(MaxTokensFinish), "max-tokens")]
[JsonDerivedType(typeof(AbortedFinish), "aborted")]
[JsonDerivedType(typeof(ErrorFinish), "error")]
public abstract record FinishReason;

/// <summary>The model finished its turn on its own.</summary>
public sealed record StopFinish : FinishReason
{
    /// <summary>The shared instance; the reason carries no state.</summary>
    public static StopFinish Instance { get; } = new();
}

/// <summary>The model stopped because it wants tools run.</summary>
public sealed record ToolCallsFinish : FinishReason
{
    /// <summary>The shared instance; the reason carries no state.</summary>
    public static ToolCallsFinish Instance { get; } = new();
}

/// <summary>The model hit its output-token ceiling. Any tool calls it had begun are unsafe to run.</summary>
public sealed record MaxTokensFinish : FinishReason
{
    /// <summary>The shared instance; the reason carries no state.</summary>
    public static MaxTokensFinish Instance { get; } = new();
}

/// <summary>The caller cancelled before the model finished.</summary>
/// <param name="Failure">The cancellation, in failure terms.</param>
public sealed record AbortedFinish(LlmFailure Failure) : FinishReason;

/// <summary>The request failed.</summary>
/// <param name="Failure">What went wrong.</param>
public sealed record ErrorFinish(LlmFailure Failure) : FinishReason;

/// <summary>
/// One unit of a model stream.
/// </summary>
/// <remarks>
/// The protocol is fixed: a block opens with <see cref="BlockStartChunk" />, grows
/// through deltas carrying the same index, and closes with
/// <see cref="BlockEndChunk" /> carrying the assembled block. A
/// <see cref="UsageChunk" /> precedes the terminal <see cref="FinishChunk" />, and
/// nothing follows the finish.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BlockStartChunk), "block-start")]
[JsonDerivedType(typeof(TextDeltaChunk), "text-delta")]
[JsonDerivedType(typeof(ReasoningDeltaChunk), "reasoning-delta")]
[JsonDerivedType(typeof(ToolCallDeltaChunk), "tool-call-delta")]
[JsonDerivedType(typeof(BlockEndChunk), "block-end")]
[JsonDerivedType(typeof(UsageChunk), "usage")]
[JsonDerivedType(typeof(FinishChunk), "finish")]
public abstract record StreamChunk;

/// <summary>A new content block begins.</summary>
/// <param name="Index">Correlates every later delta and the block's end.</param>
/// <param name="BlockType">Which kind of block is opening: <c>text</c>, <c>reasoning</c>, or <c>tool-call</c>.</param>
public sealed record BlockStartChunk(int Index, string BlockType) : StreamChunk;

/// <summary>More visible text for an open block.</summary>
/// <param name="Index">The block being extended.</param>
/// <param name="Text">The fragment to append.</param>
public sealed record TextDeltaChunk(int Index, string Text) : StreamChunk;

/// <summary>More thinking text for an open block.</summary>
/// <param name="Index">The block being extended.</param>
/// <param name="Text">The fragment to append.</param>
public sealed record ReasoningDeltaChunk(int Index, string Text) : StreamChunk;

/// <summary>More of a tool call's arguments.</summary>
/// <param name="Index">The block being extended.</param>
/// <param name="Id">The call id, known from the call's first fragment.</param>
/// <param name="Name">The tool name, present once the provider has sent it.</param>
/// <param name="ArgumentsDelta">Raw JSON text to append to the arguments string.</param>
public sealed record ToolCallDeltaChunk(int Index, CallId Id, string? Name, string ArgumentsDelta) : StreamChunk;

/// <summary>A content block is complete.</summary>
/// <param name="Index">The block that closed.</param>
/// <param name="Block">The assembled block, so a consumer need not accumulate deltas itself.</param>
public sealed record BlockEndChunk(int Index, ContentBlock Block) : StreamChunk;

/// <summary>Token accounting for the request, sent before the finish.</summary>
/// <param name="Usage">The disjoint counts.</param>
public sealed record UsageChunk(TokenUsage Usage) : StreamChunk;

/// <summary>The stream's terminal chunk.</summary>
/// <param name="Reason">Why it ended.</param>
/// <param name="ReplayState">Adapter-private metadata to replay on later requests.</param>
public sealed record FinishChunk(FinishReason Reason, object? ReplayState = null) : StreamChunk;

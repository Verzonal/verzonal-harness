using System.Text.Json.Serialization;

namespace Dsh.Llm.DeepSeek;

/// <summary>One tool the model may call, as the provider expects it.</summary>
/// <param name="Type">Always <c>function</c>.</param>
/// <param name="Function">The tool's name, description, and parameter schema.</param>
public sealed record WireTool(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] WireFunction Function);

/// <summary>A tool's declaration.</summary>
/// <param name="Name">The name the model calls.</param>
/// <param name="Description">What it does.</param>
/// <param name="Parameters">Its JSON Schema.</param>
public sealed record WireFunction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameters")] IReadOnlyDictionary<string, object?> Parameters);

/// <summary>A completed tool call replayed on an assistant history message.</summary>
/// <param name="Id">The call's id.</param>
/// <param name="Type">Always <c>function</c>.</param>
/// <param name="Function">Its name and raw argument string.</param>
public sealed record WireToolCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] WireToolCallFunction Function);

/// <summary>A tool call's name and arguments.</summary>
/// <param name="Name">The tool that was called.</param>
/// <param name="Arguments">The raw JSON argument string.</param>
public sealed record WireToolCallFunction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] string Arguments);

/// <summary>One history entry as the provider expects it.</summary>
/// <param name="Role">Who is speaking: system, user, assistant, or tool.</param>
/// <param name="Content">
/// The text. An assistant turn with no prose sends an empty string, never null —
/// a null there is rejected by the API, and since the turn is durable in the session
/// log it would break every later turn of that session too.
/// </param>
/// <param name="ReasoningContent">The turn's thinking, replayed on turns that had any.</param>
/// <param name="ToolCalls">The calls an assistant turn made.</param>
/// <param name="ToolCallId">Which call a tool message answers.</param>
public sealed record WireMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("reasoning_content")] string? ReasoningContent = null,
    [property: JsonPropertyName("tool_calls")] IReadOnlyList<WireToolCall>? ToolCalls = null,
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId = null);

/// <summary>Whether the model should think before answering.</summary>
/// <param name="Type">Either <c>enabled</c> or <c>disabled</c>.</param>
public sealed record WireThinking([property: JsonPropertyName("type")] string Type);

/// <summary>Asks the provider to report token accounting with the stream.</summary>
/// <param name="IncludeUsage">Always true.</param>
public sealed record WireStreamOptions([property: JsonPropertyName("include_usage")] bool IncludeUsage);

/// <summary>One chat-completions request.</summary>
/// <param name="Model">The model to call.</param>
/// <param name="Messages">The history.</param>
/// <param name="Stream">Always true; the harness only streams.</param>
/// <param name="StreamOptions">Asks for usage alongside the stream.</param>
/// <param name="Thinking">Whether to think, when the route offers a choice.</param>
/// <param name="ReasoningEffort">How hard to think.</param>
/// <param name="Tools">What the model may call.</param>
/// <param name="Temperature">Sampling temperature.</param>
/// <param name="MaxTokens">The output ceiling.</param>
/// <param name="Stop">Stop sequences.</param>
public sealed record WireRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<WireMessage> Messages,
    [property: JsonPropertyName("stream")] bool Stream,
    [property: JsonPropertyName("stream_options")] WireStreamOptions StreamOptions,
    [property: JsonPropertyName("thinking")] WireThinking? Thinking = null,
    [property: JsonPropertyName("reasoning_effort")] string? ReasoningEffort = null,
    [property: JsonPropertyName("tools")] IReadOnlyList<WireTool>? Tools = null,
    [property: JsonPropertyName("temperature")] double? Temperature = null,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens = null,
    [property: JsonPropertyName("stop")] IReadOnlyList<string>? Stop = null);

/// <summary>A streamed fragment of one tool call.</summary>
/// <param name="Index">Disambiguates parallel calls; stable across a call's fragments.</param>
/// <param name="Id">Present on the call's first fragment only.</param>
/// <param name="Function">The name and the argument fragment.</param>
public sealed record WireToolCallDelta(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("function")] WireToolCallFunctionDelta? Function);

/// <summary>A tool call's streamed name and argument fragment.</summary>
/// <param name="Name">Present on the call's first fragment only.</param>
/// <param name="Arguments">Argument text to append.</param>
public sealed record WireToolCallFunctionDelta(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("arguments")] string? Arguments);

/// <summary>The incremental content of one streamed choice.</summary>
/// <param name="Content">Visible text.</param>
/// <param name="ReasoningContent">Thinking text.</param>
/// <param name="ToolCalls">Tool-call fragments.</param>
public sealed record WireDelta(
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("reasoning_content")] string? ReasoningContent,
    [property: JsonPropertyName("tool_calls")] IReadOnlyList<WireToolCallDelta>? ToolCalls);

/// <summary>One streamed choice.</summary>
/// <param name="Delta">What arrived in this chunk.</param>
/// <param name="FinishReason">Non-null only on the choice's terminal chunk.</param>
public sealed record WireChoice(
    [property: JsonPropertyName("delta")] WireDelta? Delta,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

/// <summary>
/// The provider's token accounting.
/// </summary>
/// <remarks>
/// <c>prompt_tokens</c> already includes cache hits, so the hits are subtracted when
/// this is mapped: the harness keeps its counts disjoint, and summing them must not
/// double-count.
/// </remarks>
/// <param name="PromptTokens">Input tokens, cache hits included.</param>
/// <param name="CompletionTokens">Generated tokens.</param>
/// <param name="PromptCacheHitTokens">Input tokens served from cache.</param>
/// <param name="PromptTokensDetails">The compatibility spelling of the hit count.</param>
/// <param name="CompletionTokensDetails">Where the reasoning-token count arrives.</param>
public sealed record WireUsage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("prompt_cache_hit_tokens")] int? PromptCacheHitTokens = null,
    [property: JsonPropertyName("prompt_tokens_details")] WirePromptDetails? PromptTokensDetails = null,
    [property: JsonPropertyName("completion_tokens_details")] WireCompletionDetails? CompletionTokensDetails = null);

/// <summary>The compatibility spelling of the cache-hit count.</summary>
/// <param name="CachedTokens">Input tokens served from cache.</param>
public sealed record WirePromptDetails([property: JsonPropertyName("cached_tokens")] int? CachedTokens);

/// <summary>Where the reasoning-token count arrives.</summary>
/// <param name="ReasoningTokens">Output tokens spent thinking.</param>
public sealed record WireCompletionDetails([property: JsonPropertyName("reasoning_tokens")] int? ReasoningTokens);

/// <summary>One parsed stream chunk.</summary>
/// <param name="Choices">The streamed choices; requests always ask for one.</param>
/// <param name="Usage">Accounting, on the finish chunk or a trailing usage-only chunk.</param>
public sealed record WireChunk(
    [property: JsonPropertyName("choices")] IReadOnlyList<WireChoice>? Choices,
    [property: JsonPropertyName("usage")] WireUsage? Usage);

/// <summary>A non-success response body.</summary>
/// <param name="Error">What the provider said went wrong.</param>
public sealed record WireErrorBody([property: JsonPropertyName("error")] WireErrorDetail? Error);

/// <summary>The provider's description of a failure.</summary>
/// <param name="Message">What went wrong.</param>
/// <param name="Type">Its category.</param>
/// <param name="Code">Its machine-readable code.</param>
public sealed record WireErrorDetail(
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("code")] string? Code);

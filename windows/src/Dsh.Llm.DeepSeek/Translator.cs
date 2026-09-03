using System.Text;

namespace Dsh.Llm.DeepSeek;

/// <summary>
/// Turns the provider's streamed deltas into the harness's chunk protocol.
/// </summary>
/// <remarks>
/// Everything terminal is held back until the sentinel arrives, so the harness sees
/// exactly one finish and it is the last thing on the stream. Blocks are numbered in
/// the order they are first seen, and a tool call's fragments are collected by the
/// provider's own index so parallel calls never bleed into one another.
/// </remarks>
public sealed class StreamTranslator
{
    private sealed class OpenBlock
    {
        public required int Index { get; init; }
        public required string Kind { get; init; }
        public StringBuilder Text { get; } = new();
        public string? CallId { get; set; }
        public string? Name { get; set; }
    }

    private readonly List<OpenBlock> _order = [];
    private readonly Dictionary<int, OpenBlock> _toolBlocks = [];
    private OpenBlock? _text;
    private OpenBlock? _reasoning;
    private int _nextIndex;
    private TokenUsage? _usage;
    private FinishReason? _finish;

    /// <summary>
    /// Fold one parsed chunk, emitting whatever it makes available.
    /// </summary>
    /// <param name="chunk">The provider's chunk.</param>
    /// <returns>The harness chunks it produced, in order.</returns>
    public IEnumerable<StreamChunk> Push(WireChunk chunk)
    {
        var emitted = new List<StreamChunk>();

        // Latest wins: accounting can arrive attached to the finish chunk or as a
        // trailing usage-only chunk, and only the last one is complete.
        if (chunk.Usage is { } usage) _usage = MapUsage(usage);

        foreach (var choice in chunk.Choices ?? [])
        {
            var delta = choice.Delta;

            // Thinking is interleaved before prose, and its first fragment is an empty
            // string that must not open a block a reader would then see as blank.
            if (!string.IsNullOrEmpty(delta?.ReasoningContent))
            {
                if (_reasoning is null)
                {
                    _reasoning = Open("reasoning");
                    emitted.Add(new BlockStartChunk(_reasoning.Index, "reasoning"));
                }

                _reasoning.Text.Append(delta.ReasoningContent);
                emitted.Add(new ReasoningDeltaChunk(_reasoning.Index, delta.ReasoningContent));
            }

            if (!string.IsNullOrEmpty(delta?.Content))
            {
                if (_text is null)
                {
                    _text = Open("text");
                    emitted.Add(new BlockStartChunk(_text.Index, "text"));
                }

                _text.Text.Append(delta.Content);
                emitted.Add(new TextDeltaChunk(_text.Index, delta.Content));
            }

            foreach (var call in delta?.ToolCalls ?? [])
            {
                if (!_toolBlocks.TryGetValue(call.Index, out var block))
                {
                    block = Open("tool-call");
                    _toolBlocks[call.Index] = block;
                    emitted.Add(new BlockStartChunk(block.Index, "tool-call"));
                }

                if (call.Id is not null) block.CallId = call.Id;
                if (call.Function?.Name is not null) block.Name = call.Function.Name;

                var fragment = call.Function?.Arguments ?? string.Empty;
                block.Text.Append(fragment);
                emitted.Add(new ToolCallDeltaChunk(
                    block.Index,
                    new CallId(block.CallId ?? string.Empty),
                    block.Name,
                    fragment));
            }

            if (choice.FinishReason is { } reason) _finish = MapFinish(reason);
        }

        return emitted;
    }

    /// <summary>
    /// Close the stream once the sentinel arrives.
    /// </summary>
    /// <returns>
    /// A block end for each block in first-seen order, then usage, then exactly one
    /// finish. A completion that opened no block at all becomes a retryable
    /// empty-response failure rather than an empty assistant turn.
    /// </returns>
    public IEnumerable<StreamChunk> Complete()
    {
        var emitted = new List<StreamChunk>();

        foreach (var block in _order)
        {
            emitted.Add(new BlockEndChunk(block.Index, Close(block)));
        }

        if (_usage is { } usage) emitted.Add(new UsageChunk(usage));

        var finish = _finish ?? StopFinish.Instance;
        if (finish is StopFinish && _order.Count == 0)
        {
            finish = new ErrorFinish(new LlmFailure(
                "the model returned a completed response with no content",
                LlmErrorCodes.EmptyResponse));
        }

        emitted.Add(new FinishChunk(finish));
        return emitted;
    }

    private OpenBlock Open(string kind)
    {
        var block = new OpenBlock { Index = _nextIndex++, Kind = kind };
        _order.Add(block);
        return block;
    }

    private static ContentBlock Close(OpenBlock block) => block.Kind switch
    {
        "reasoning" => new ReasoningBlock(block.Text.ToString()),
        "tool-call" => new ToolCallBlock(
            new CallId(block.CallId ?? $"call-{block.Index}"),
            block.Name ?? string.Empty,
            block.Text.ToString()),
        _ => new TextBlock(block.Text.ToString()),
    };

    /// <summary>
    /// Classify the provider's stop reason.
    /// </summary>
    /// <param name="reason">What the provider reported.</param>
    /// <returns>The harness's equivalent; anything unrecognized is a terminal failure.</returns>
    internal static FinishReason MapFinish(string reason) => reason switch
    {
        "stop" => StopFinish.Instance,
        "tool_calls" => ToolCallsFinish.Instance,
        "length" => MaxTokensFinish.Instance,
        _ => new ErrorFinish(new LlmFailure(
            $"the model stopped for an unrecognized reason: {reason}",
            reason.ToUpperInvariant())),
    };

    /// <summary>
    /// Convert the provider's accounting into disjoint counts.
    /// </summary>
    /// <param name="usage">What the provider reported.</param>
    /// <returns>The harness's counts, with cache hits subtracted from the input total.</returns>
    internal static TokenUsage MapUsage(WireUsage usage)
    {
        var cacheRead = usage.PromptTokensDetails?.CachedTokens ?? usage.PromptCacheHitTokens;
        return new TokenUsage(
            usage.PromptTokens - (cacheRead ?? 0),
            usage.CompletionTokens,
            cacheRead,
            null,
            usage.CompletionTokensDetails?.ReasoningTokens);
    }
}

using System.Text;

namespace Dsh.Llm;

/// <summary>
/// Turns a chunk stream into the content blocks of one assistant message.
/// </summary>
/// <remarks>
/// Tolerant of providers that send only deltas: a delta for an unopened index opens
/// the block itself. Deltas arriving after a block has ended are ignored, and the
/// first <see cref="BlockEndChunk" /> for an index wins, so a provider that both
/// streams and re-sends a block cannot duplicate it.
/// </remarks>
public sealed class BlockAssembler
{
    private sealed class OpenBlock
    {
        public required int Index { get; init; }
        public required string BlockType { get; init; }
        public StringBuilder Text { get; } = new();
        public CallId? CallId { get; set; }
        public string? Name { get; set; }
        public ContentBlock? Closed { get; set; }
    }

    private readonly Dictionary<int, OpenBlock> _blocks = [];
    private readonly List<OpenBlock> _order = [];

    /// <summary>The token accounting reported by the stream, when it reported any.</summary>
    public TokenUsage? Usage { get; private set; }

    /// <summary>Why the stream ended; null until a terminal chunk arrives.</summary>
    public FinishReason? Finish { get; private set; }

    /// <summary>Adapter-private replay metadata carried by the terminal chunk.</summary>
    public object? ReplayState { get; private set; }

    /// <summary>
    /// Fold one chunk into the message being assembled.
    /// </summary>
    /// <param name="chunk">The chunk to absorb.</param>
    public void Push(StreamChunk chunk)
    {
        switch (chunk)
        {
            case BlockStartChunk start:
                Open(start.Index, start.BlockType);
                break;
            case TextDeltaChunk text:
                Append(text.Index, "text", text.Text);
                break;
            case ReasoningDeltaChunk reasoning:
                Append(reasoning.Index, "reasoning", reasoning.Text);
                break;
            case ToolCallDeltaChunk call:
            {
                var block = Open(call.Index, "tool-call");
                if (block.Closed is null)
                {
                    if (!string.IsNullOrEmpty(call.Id.Value)) block.CallId = call.Id;
                    if (call.Name is not null) block.Name = call.Name;
                    block.Text.Append(call.ArgumentsDelta);
                }

                break;
            }

            case BlockEndChunk end:
            {
                var block = Open(end.Index, BlockTypeOf(end.Block));
                block.Closed ??= end.Block;
                break;
            }

            case UsageChunk usage:
                Usage = usage.Usage;
                break;
            case FinishChunk finish:
                Finish = finish.Reason;
                ReplayState = finish.ReplayState;
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// The assembled content, in the order the blocks were first seen.
    /// </summary>
    /// <returns>
    /// Every block the stream produced — except that a <see cref="MaxTokensFinish" />
    /// drops tool calls, because a call truncated at the ceiling cannot be run safely.
    /// </returns>
    public IReadOnlyList<ContentBlock> Blocks()
    {
        var truncated = Finish is MaxTokensFinish;
        var result = new List<ContentBlock>(_order.Count);
        foreach (var block in _order)
        {
            var closed = Close(block);
            if (truncated && closed is ToolCallBlock) continue;
            result.Add(closed);
        }

        return result;
    }

    /// <summary>
    /// The prose delivered before a cancellation, for recording what the user actually saw.
    /// </summary>
    /// <returns>
    /// Only text and reasoning blocks with non-whitespace content; tool calls are
    /// omitted because an interrupted turn never dispatched them.
    /// </returns>
    public IReadOnlyList<ContentBlock> InterruptedBlocks()
    {
        var result = new List<ContentBlock>();
        foreach (var block in _order)
        {
            var closed = Close(block);
            switch (closed)
            {
                case TextBlock text when !string.IsNullOrWhiteSpace(text.Text):
                    result.Add(text);
                    break;
                case ReasoningBlock reasoning when !string.IsNullOrWhiteSpace(reasoning.Text):
                    result.Add(reasoning);
                    break;
                default:
                    break;
            }
        }

        return result;
    }

    private OpenBlock Open(int index, string blockType)
    {
        if (_blocks.TryGetValue(index, out var existing)) return existing;
        var block = new OpenBlock { Index = index, BlockType = blockType };
        _blocks[index] = block;
        _order.Add(block);
        return block;
    }

    private void Append(int index, string blockType, string text)
    {
        var block = Open(index, blockType);
        if (block.Closed is null) block.Text.Append(text);
    }

    private static ContentBlock Close(OpenBlock block)
    {
        if (block.Closed is not null) return block.Closed;
        var text = block.Text.ToString();
        ContentBlock closed = block.BlockType switch
        {
            "reasoning" => new ReasoningBlock(text),
            "tool-call" => new ToolCallBlock(
                block.CallId ?? new CallId($"call-{block.Index}"),
                block.Name ?? string.Empty,
                text),
            _ => new TextBlock(text),
        };
        block.Closed = closed;
        return closed;
    }

    private static string BlockTypeOf(ContentBlock block) => block switch
    {
        ReasoningBlock => "reasoning",
        ToolCallBlock => "tool-call",
        ImageBlock => "image",
        ToolResultBlock => "tool-result",
        _ => "text",
    };
}

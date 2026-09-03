using System.Text.Json.Serialization;

namespace Dsh.Llm;

/// <summary>
/// One part of a message's content. The set is closed at the model layer: a
/// provider adapter translates these into its own wire format and never sees a
/// harness-private variant.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(ReasoningBlock), "reasoning")]
[JsonDerivedType(typeof(ImageBlock), "image")]
[JsonDerivedType(typeof(ToolCallBlock), "tool-call")]
[JsonDerivedType(typeof(ToolResultBlock), "tool-result")]
public abstract record ContentBlock;

/// <summary>Visible prose.</summary>
/// <param name="Text">The text itself.</param>
public sealed record TextBlock(string Text) : ContentBlock;

/// <summary>
/// Thinking-mode chain of thought. Kept separate from <see cref="TextBlock" />
/// because a UI hides it by default and a provider replays it on its own field.
/// </summary>
/// <param name="Text">The reasoning text.</param>
public sealed record ReasoningBlock(string Text) : ContentBlock;

/// <summary>
/// An image carried by reference rather than by value, so the durable log never
/// holds the bytes.
/// </summary>
/// <param name="AttachmentId">Content-addressed id resolved by the attachment store.</param>
/// <param name="MediaType">The image's IANA media type, for example <c>image/png</c>.</param>
public sealed record ImageBlock(string AttachmentId, string MediaType) : ContentBlock;

/// <summary>
/// One tool invocation the model requested.
/// </summary>
/// <param name="Id">Pairs this call with its result.</param>
/// <param name="Name">The tool name as the model wrote it.</param>
/// <param name="Arguments">
/// The raw JSON string the model produced, never parsed at this layer: a malformed
/// value has to reach the tool pipeline intact so the model can be told what it got
/// wrong.
/// </param>
public sealed record ToolCallBlock(CallId Id, string Name, string Arguments) : ContentBlock;

/// <summary>
/// The outcome of one tool call, carried inside a user-role message.
/// </summary>
/// <param name="ToolCallId">The call this answers.</param>
/// <param name="Content">The model-facing result content.</param>
/// <param name="IsError">Whether the call failed; the content then carries the error text.</param>
public sealed record ToolResultBlock(
    CallId ToolCallId,
    IReadOnlyList<ContentBlock> Content,
    bool IsError = false) : ContentBlock;

/// <summary>Helpers over a message's content list.</summary>
public static class ContentBlocks
{
    /// <summary>
    /// Concatenate every text block, ignoring reasoning, images, and tool traffic.
    /// </summary>
    /// <param name="content">The blocks to flatten.</param>
    /// <returns>The joined visible text, empty when the content carries none.</returns>
    public static string FlattenText(IEnumerable<ContentBlock> content)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var block in content)
        {
            if (block is TextBlock text) builder.Append(text.Text);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Concatenate every reasoning block.
    /// </summary>
    /// <param name="content">The blocks to flatten.</param>
    /// <returns>The joined reasoning text, empty when the content carries none.</returns>
    public static string FlattenReasoning(IEnumerable<ContentBlock> content)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var block in content)
        {
            if (block is ReasoningBlock reasoning) builder.Append(reasoning.Text);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Select the tool calls in a message's content, in model order.
    /// </summary>
    /// <param name="content">The blocks to filter.</param>
    /// <returns>Every tool-call block, in order.</returns>
    public static IReadOnlyList<ToolCallBlock> ToolCalls(IEnumerable<ContentBlock> content)
        => [.. content.OfType<ToolCallBlock>()];
}

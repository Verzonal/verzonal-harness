using System.Text.Json.Serialization;

namespace Dsh.Llm;

/// <summary>
/// Who a message speaks as. There are only three: a tool result is not a role of
/// its own, it is a user message carrying a <see cref="ToolResultBlock" />.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MessageRole>))]
public enum MessageRole
{
    /// <summary>Instructions framing the conversation.</summary>
    System,

    /// <summary>Input to the model: a human prompt, injected context, or a tool result.</summary>
    User,

    /// <summary>The model's own output.</summary>
    Assistant,
}

/// <summary>
/// Where a message came from. Producers are distinguished here rather than by
/// inspecting content, so a UI can label a synthetic context message without
/// pattern-matching its text.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(UserMessageSource), "user")]
[JsonDerivedType(typeof(PluginMessageSource), "plugin")]
[JsonDerivedType(typeof(ModelMessageSource), "model")]
[JsonDerivedType(typeof(ToolMessageSource), "tool")]
public abstract record MessageSource;

/// <summary>A direct human prompt.</summary>
public sealed record UserMessageSource : MessageSource
{
    /// <summary>The shared instance; the source carries no state.</summary>
    public static UserMessageSource Instance { get; } = new();
}

/// <summary>
/// How a plugin-produced context message is meant to be read, so a UI can present
/// it without guessing from its text.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ContextForm>))]
public enum ContextForm
{
    /// <summary>Project or directory instructions.</summary>
    Instructions,

    /// <summary>A list of available things, such as a skill catalog.</summary>
    Catalog,

    /// <summary>A point-in-time snapshot that supersedes earlier ones.</summary>
    Snapshot,

    /// <summary>A short notification about something that happened.</summary>
    Notice,

    /// <summary>Content relayed verbatim from elsewhere.</summary>
    Relay,

    /// <summary>Material recalled from another session.</summary>
    Recall,
}

/// <summary>
/// Context injected by a plugin rather than typed by a human.
/// </summary>
/// <param name="Plugin">The producing plugin's name.</param>
/// <param name="Form">How the content is meant to be read.</param>
/// <param name="Summary">One line a UI can show while the body is collapsed.</param>
public sealed record PluginMessageSource(string Plugin, ContextForm Form, string? Summary = null) : MessageSource;

/// <summary>
/// The exact route that produced an assistant message.
/// </summary>
/// <param name="Provider">The registered provider route.</param>
/// <param name="Model">The provider-owned model id.</param>
/// <param name="ReplayState">
/// Adapter-private metadata replayed on later requests. Opaque to the harness, and
/// scrubbed before reaching an adapter that did not produce it.
/// </param>
public sealed record ModelMessageSource(string Provider, string Model, object? ReplayState = null) : MessageSource;

/// <summary>
/// A tool's result, which rides on a user-role message.
/// </summary>
/// <param name="CallId">The call this message answers.</param>
public sealed record ToolMessageSource(CallId CallId) : MessageSource;

/// <summary>
/// One entry of model history. Messages are immutable once built: the session log
/// stores them verbatim and derived history hands out the same instances.
/// </summary>
/// <param name="Id">Stable identity, used by the inbox to address queued items.</param>
/// <param name="Role">Who the message speaks as.</param>
/// <param name="Content">The ordered content parts.</param>
/// <param name="Source">Which producer created it.</param>
public sealed record Message(
    MessageId Id,
    MessageRole Role,
    IReadOnlyList<ContentBlock> Content,
    MessageSource Source)
{
    /// <summary>
    /// Build a human prompt.
    /// </summary>
    /// <param name="content">The prompt's content parts.</param>
    /// <returns>A user-role message sourced to the human.</returns>
    public static Message User(params ContentBlock[] content)
        => new(MessageId.New(), MessageRole.User, content, UserMessageSource.Instance);

    /// <summary>
    /// Build a human prompt from plain text.
    /// </summary>
    /// <param name="text">The prompt text.</param>
    /// <returns>A user-role message sourced to the human.</returns>
    public static Message UserText(string text) => User(new TextBlock(text));

    /// <summary>
    /// Build a context message a plugin injects into the model's view.
    /// </summary>
    /// <param name="plugin">The producing plugin's name.</param>
    /// <param name="form">How the content is meant to be read.</param>
    /// <param name="content">The content parts, already carrying any framing the producer wants.</param>
    /// <param name="summary">One line a UI can show while the body is collapsed.</param>
    /// <returns>A user-role message sourced to the plugin.</returns>
    public static Message Context(
        string plugin,
        ContextForm form,
        IReadOnlyList<ContentBlock> content,
        string? summary = null)
        => new(MessageId.New(), MessageRole.User, content, new PluginMessageSource(plugin, form, summary));

    /// <summary>
    /// Build an assistant message for one step's output.
    /// </summary>
    /// <param name="content">The assembled content parts.</param>
    /// <param name="source">The route that produced it.</param>
    /// <returns>An assistant-role message.</returns>
    public static Message Assistant(IReadOnlyList<ContentBlock> content, ModelMessageSource source)
        => new(MessageId.New(), MessageRole.Assistant, content, source);

    /// <summary>
    /// Build the message carrying one tool call's result.
    /// </summary>
    /// <param name="callId">The call being answered.</param>
    /// <param name="content">The model-facing result content.</param>
    /// <param name="isError">Whether the call failed.</param>
    /// <returns>
    /// A user-role message whose single block is the tool result — the only shape
    /// the session log accepts for a tool outcome.
    /// </returns>
    public static Message ToolResult(CallId callId, IReadOnlyList<ContentBlock> content, bool isError)
        => new(
            MessageId.New(),
            MessageRole.User,
            [new ToolResultBlock(callId, content, isError)],
            new ToolMessageSource(callId));

    /// <summary>The message's visible text, with reasoning and tool traffic left out.</summary>
    public string Text => ContentBlocks.FlattenText(Content);
}

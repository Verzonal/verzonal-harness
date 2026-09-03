using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.Llm;
using Dsh.Tools;

namespace Dsh.App.Core;

/// <summary>One row of the conversation.</summary>
public abstract partial class ConversationNode : ObservableObject
{
    /// <summary>The log position this row was created from, for stable ordering.</summary>
    public int Seq { get; init; }
}

/// <summary>Input the model was given: a person's prompt, or context a plugin injected.</summary>
public sealed partial class UserNode : ConversationNode
{
    /// <summary>The message text.</summary>
    public required string Text { get; init; }

    /// <summary>Whether a plugin produced this rather than a person typing it.</summary>
    public bool IsContext { get; init; }

    /// <summary>The producing plugin, when this is injected context.</summary>
    public string? Producer { get; init; }

    /// <summary>How the injected content is meant to be read.</summary>
    public ContextForm? Form { get; init; }

    /// <summary>One line to show while an injected row is collapsed.</summary>
    public string Summary => IsContext
        ? $"{Producer ?? "context"} · {Form?.ToString().ToLowerInvariant() ?? "context"}"
        : string.Empty;

    /// <summary>The message as renderable blocks.</summary>
    public IReadOnlyList<MarkdownBlock> Blocks => MarkdownParser.Parse(Text);
}

/// <summary>
/// The model's own output for one step.
/// </summary>
/// <remarks>
/// Grows while the model streams and settles when the step's message is logged. The
/// thinking is kept separate from the answer so a view can collapse it: it is
/// usually noise to a reader, and occasionally exactly what they need.
/// </remarks>
public sealed partial class AssistantNode : ConversationNode
{
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _reasoning = new();

    [ObservableProperty]
    private bool _isStreaming = true;

    [ObservableProperty]
    private bool _wasInterrupted;

    /// <summary>The visible answer so far.</summary>
    public string Text => _text.ToString();

    /// <summary>The thinking so far.</summary>
    public string Reasoning => _reasoning.ToString();

    /// <summary>Whether the model thought before answering.</summary>
    public bool HasReasoning => _reasoning.Length > 0;

    /// <summary>Whether there is an answer to draw yet.</summary>
    public bool HasText => _text.Length > 0;

    /// <summary>The answer as renderable blocks.</summary>
    public IReadOnlyList<MarkdownBlock> Blocks => MarkdownParser.Parse(Text);

    /// <summary>The last non-blank line of the thinking, shown while it is collapsed.</summary>
    public string ReasoningSummary
    {
        get
        {
            var lines = _reasoning.ToString().Split('\n');
            for (var index = lines.Length - 1; index >= 0; index--)
            {
                if (!string.IsNullOrWhiteSpace(lines[index])) return lines[index].Trim();
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// Append streamed answer text.
    /// </summary>
    /// <param name="text">The fragment to add.</param>
    public void AppendText(string text)
    {
        _text.Append(text);
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(HasText));
        OnPropertyChanged(nameof(Blocks));
    }

    /// <summary>
    /// Append streamed thinking.
    /// </summary>
    /// <param name="text">The fragment to add.</param>
    public void AppendReasoning(string text)
    {
        _reasoning.Append(text);
        OnPropertyChanged(nameof(Reasoning));
        OnPropertyChanged(nameof(HasReasoning));
        OnPropertyChanged(nameof(ReasoningSummary));
    }

    /// <summary>
    /// Settle the row against the step's logged message.
    /// </summary>
    /// <param name="message">The assembled message.</param>
    /// <param name="interrupted">Whether this records a prefix delivered before a cancellation.</param>
    /// <remarks>
    /// The logged message is authoritative: a row built from chunks is replaced by it,
    /// so what is shown always matches what a replay would show.
    /// </remarks>
    public void Settle(Message message, bool interrupted)
    {
        var text = ContentBlocks.FlattenText(message.Content);
        var reasoning = ContentBlocks.FlattenReasoning(message.Content);

        _text.Clear();
        _text.Append(text);
        _reasoning.Clear();
        _reasoning.Append(reasoning);

        IsStreaming = false;
        WasInterrupted = interrupted;

        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(HasText));
        OnPropertyChanged(nameof(Blocks));
        OnPropertyChanged(nameof(Reasoning));
        OnPropertyChanged(nameof(HasReasoning));
        OnPropertyChanged(nameof(ReasoningSummary));
    }
}

/// <summary>Where one tool call has got to.</summary>
public enum ToolNodeState
{
    /// <summary>Requested, not yet answered.</summary>
    Running,

    /// <summary>Answered successfully.</summary>
    Completed,

    /// <summary>Answered with a failure.</summary>
    Failed,
}

/// <summary>
/// One tool call and its outcome.
/// </summary>
/// <remarks>
/// The card is chosen by the render intent the tool itself declares, so a view can
/// draw a call it has never heard of. Both views are recomputed from the log rather
/// than stored, so replaying a session redraws exactly the same cards.
/// </remarks>
public sealed partial class ToolNode : ConversationNode
{
    [ObservableProperty]
    private ToolNodeState _state = ToolNodeState.Running;

    [ObservableProperty]
    private ToolResultView? _resultView;

    [ObservableProperty]
    private string _resultText = string.Empty;

    /// <summary>The call this row is about.</summary>
    public required CallId CallId { get; init; }

    /// <summary>The tool that was called.</summary>
    public required string ToolName { get; init; }

    /// <summary>The raw argument string the model produced.</summary>
    public required string Arguments { get; init; }

    /// <summary>How the pending call should be drawn, when the tool said.</summary>
    public ToolCallView? CallView { get; init; }

    /// <summary>The card's header.</summary>
    public string Title => CallView switch
    {
        GenericCallView generic => generic.Title,
        TerminalCallView terminal => terminal.Title,
        DiffCallView diff => diff.Title,
        _ => ToolName,
    };

    /// <summary>Whether the call is still waiting for its result.</summary>
    public bool IsRunning => State == ToolNodeState.Running;

    /// <summary>Whether the call came back an error.</summary>
    public bool HasFailed => State == ToolNodeState.Failed;

    /// <summary>Whether this row draws as a terminal.</summary>
    public bool IsTerminal => CallView is TerminalCallView || ResultView is TerminalResultView;

    /// <summary>Whether this row draws as a diff.</summary>
    public bool IsDiff => CallView is DiffCallView || ResultView is DiffResultView;

    /// <summary>The files this call touches, for an editor to follow along.</summary>
    public IReadOnlyList<FileLocation> Locations => CallView switch
    {
        GenericCallView generic => generic.Locations ?? [],
        DiffCallView diff => diff.Locations ?? [],
        _ => [],
    };

    /// <summary>
    /// Settle the row against the logged result.
    /// </summary>
    /// <param name="view">The tool's own result presentation, when it offered one.</param>
    /// <param name="text">The result's flattened content, shown when no view applies.</param>
    /// <param name="isError">Whether the call failed.</param>
    public void Settle(ToolResultView? view, string text, bool isError)
    {
        ResultView = view;
        ResultText = text;
        State = isError ? ToolNodeState.Failed : ToolNodeState.Completed;

        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(IsTerminal));
        OnPropertyChanged(nameof(IsDiff));
    }
}

/// <summary>A turn that ended badly, shown where it happened.</summary>
public sealed partial class TurnFailureNode : ConversationNode
{
    /// <summary>What went wrong, in the provider's own words.</summary>
    public required string Message { get; init; }

    /// <summary>The machine-readable classification.</summary>
    public required string Code { get; init; }

    /// <summary>Whether a person stopped the turn rather than it failing.</summary>
    public bool WasCancelled { get; init; }
}

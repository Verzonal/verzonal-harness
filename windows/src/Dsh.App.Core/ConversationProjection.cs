using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Tools;

namespace Dsh.App.Core;

/// <summary>
/// Turns a session log into the rows a conversation view shows.
/// </summary>
/// <remarks>
/// This is the whole reason the app's display is trustworthy: it reads only the
/// session log, never a side channel. Live streaming and replaying a stored session
/// go down exactly the same path, so what a person sees now is what they will see
/// when they reopen the session tomorrow.
/// </remarks>
public sealed partial class ConversationProjection : ObservableObject
{
    private readonly Func<string, ITool?> _lookup;
    private readonly Dictionary<CallId, ToolNode> _calls = [];
    private AssistantNode? _streaming;
    private int _currentStep = -1;

    [ObservableProperty]
    private IReadOnlyList<TodoItem> _todos = [];

    [ObservableProperty]
    private TokenUsage? _usage;

    [ObservableProperty]
    private int? _contextWindow;

    [ObservableProperty]
    private string _route = string.Empty;

    /// <param name="lookup">
    /// Finds a tool by name so its own presentation can be used. A tool that is no
    /// longer registered simply yields no card, and the row falls back to raw text.
    /// </param>
    public ConversationProjection(Func<string, ITool?> lookup) => _lookup = lookup;

    /// <summary>The rows, in log order.</summary>
    public ObservableCollection<ConversationNode> Nodes { get; } = [];

    /// <summary>How full the model's context is, as a fraction, when the window is known.</summary>
    public double? ContextPressure => Usage is { } usage && ContextWindow is > 0
        ? Math.Min(1.0, (double)(usage.TotalInputTokens + usage.OutputTokens) / ContextWindow.Value)
        : null;

    /// <summary>
    /// Rebuild every row from a stored log.
    /// </summary>
    /// <param name="events">The log to project.</param>
    public void Replay(IReadOnlyList<SessionEvent> events)
    {
        Nodes.Clear();
        _calls.Clear();
        _streaming = null;
        _currentStep = -1;
        Todos = [];
        Usage = null;

        foreach (var entry in events) Apply(entry);
    }

    /// <summary>
    /// Fold one newly appended event.
    /// </summary>
    /// <param name="entry">The event.</param>
    public void Apply(SessionEvent entry)
    {
        switch (entry.Type)
        {
            case "user/message":
                ApplyUserMessage(entry);
                break;

            case "step/start":
                // Each step gets its own assistant row, so two model calls in one turn
                // do not run together into a single block of prose.
                _currentStep = entry.DataAs<StepStartData>().Step;
                _streaming = null;
                break;

            case "assistant/chunk":
                ApplyChunk(entry);
                break;

            case "assistant/message":
                ApplyAssistantMessage(entry);
                break;

            case "tool/call":
                ApplyToolCall(entry);
                break;

            case "tool/result":
                ApplyToolResult(entry);
                break;

            case "todo/write":
                Todos = entry.DataAs<TodoWriteData>().Todos;
                break;

            case "request/context":
            {
                var context = entry.DataAs<RequestContextData>();
                Route = $"{context.Provider}/{context.Model}";
                ContextWindow = context.ContextWindow;
                OnPropertyChanged(nameof(ContextPressure));
                break;
            }

            case "turn/end":
                ApplyTurnEnd(entry);
                break;

            default:
                break;
        }
    }

    private void ApplyUserMessage(SessionEvent entry)
    {
        var message = entry.DataAs<Message>();

        // A tool result rides on a user message; it belongs to the tool's own row, not
        // to a bubble of its own.
        if (message.Source is ToolMessageSource) return;

        var context = message.Source as PluginMessageSource;
        Nodes.Add(new UserNode
        {
            Seq = entry.Seq,
            Text = ContentBlocks.FlattenText(message.Content),
            IsContext = context is not null,
            Producer = context?.Plugin,
            Form = context?.Form,
        });
    }

    private void ApplyChunk(SessionEvent entry)
    {
        var data = entry.DataAs<AssistantChunkData>();
        switch (data.Chunk)
        {
            case TextDeltaChunk text:
                Streaming(entry.Seq).AppendText(text.Text);
                break;
            case ReasoningDeltaChunk reasoning:
                Streaming(entry.Seq).AppendReasoning(reasoning.Text);
                break;
            case UsageChunk usage:
                Usage = usage.Usage;
                OnPropertyChanged(nameof(ContextPressure));
                break;
            default:
                break;
        }
    }

    private void ApplyAssistantMessage(SessionEvent entry)
    {
        var data = entry.DataAs<AssistantMessageData>();

        // An assistant message with no content exists only to carry a truncated step's
        // accounting; showing an empty bubble for it would be noise.
        if (data.Message.Content.Count == 0)
        {
            if (data.Usage is { } accounting)
            {
                Usage = accounting;
                OnPropertyChanged(nameof(ContextPressure));
            }

            return;
        }

        var node = _streaming ?? Streaming(entry.Seq);
        node.Settle(data.Message, data.Interrupted);
        _streaming = null;

        if (data.Usage is { } usage)
        {
            Usage = usage;
            OnPropertyChanged(nameof(ContextPressure));
        }
    }

    private void ApplyToolCall(SessionEvent entry)
    {
        var call = entry.DataAs<ToolCallData>();
        var tool = _lookup(call.Name);
        var arguments = ParseArguments(call.Arguments);

        var node = new ToolNode
        {
            Seq = entry.Seq,
            CallId = call.CallId,
            ToolName = call.Name,
            Arguments = call.Arguments,
            CallView = Present(() => tool?.PresentCall(arguments)),
        };

        _calls[call.CallId] = node;
        Nodes.Add(node);

        // A tool call ends the current run of prose: whatever the model says next is a
        // new step, after the tool has answered.
        _streaming = null;
    }

    private void ApplyToolResult(SessionEvent entry)
    {
        var result = entry.DataAs<ToolResultData>();
        if (result.Message.Content is not [ToolResultBlock block]) return;
        if (!_calls.TryGetValue(block.ToolCallId, out var node)) return;

        var tool = _lookup(node.ToolName);
        var arguments = ParseArguments(node.Arguments);
        var text = ContentBlocks.FlattenText(block.Content);

        node.Settle(
            Present(() => tool?.PresentResult(
                arguments,
                new ToolResult(block.Content, block.IsError, result.Meta))),
            text,
            block.IsError);
    }

    private void ApplyTurnEnd(SessionEvent entry)
    {
        _streaming = null;
        var reason = entry.DataAs<TurnEndData>().Reason;

        switch (reason)
        {
            case ErrorTurnEnd failure:
                Nodes.Add(new TurnFailureNode
                {
                    Seq = entry.Seq,
                    Message = failure.Error.Message,
                    Code = failure.Error.Code,
                });
                break;

            case AbortedTurnEnd:
                Nodes.Add(new TurnFailureNode
                {
                    Seq = entry.Seq,
                    Message = "The turn was stopped.",
                    Code = "ABORTED",
                    WasCancelled = true,
                });
                break;

            default:
                break;
        }
    }

    private AssistantNode Streaming(int seq)
    {
        if (_streaming is not null) return _streaming;
        _streaming = new AssistantNode { Seq = seq };
        Nodes.Add(_streaming);
        return _streaming;
    }

    /// <summary>
    /// Ask a tool how to draw something, tolerating a presenter that misbehaves.
    /// </summary>
    /// <remarks>
    /// Presentation is never worth breaking the conversation view for: a throwing
    /// presenter costs its card, and the row falls back to raw text.
    /// </remarks>
    private static TView? Present<TView>(Func<TView?> present) where TView : class
    {
        try
        {
            return present();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static JsonValue ParseArguments(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return JsonValue.From(new Dictionary<string, object?>());
        try
        {
            return JsonValue.From(System.Text.Json.JsonDocument.Parse(raw).RootElement.Clone());
        }
        catch (System.Text.Json.JsonException)
        {
            return new JsonString(raw);
        }
    }
}

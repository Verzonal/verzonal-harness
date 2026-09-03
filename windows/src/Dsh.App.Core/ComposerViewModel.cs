using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.Agent;
using Dsh.Llm;
using Dsh.Session;

namespace Dsh.App.Core;

/// <summary>What pressing a key in the composer does.</summary>
public enum ComposerAction
{
    /// <summary>Nothing; the draft is empty or the composer is not ready.</summary>
    None,

    /// <summary>Add a newline to the draft.</summary>
    Newline,

    /// <summary>Queue the draft as its own turn.</summary>
    Queue,

    /// <summary>Add the draft to the running turn's next step.</summary>
    Steer,
}

/// <summary>One message waiting in the inbox, as the queue dock draws it.</summary>
/// <param name="Id">Identifies it for editing, promoting, or removing.</param>
/// <param name="Preview">Its text, flattened to one string.</param>
public sealed record QueuedMessage(MessageId Id, string Preview);

/// <summary>Which key sends and which key steers while a turn is running.</summary>
public enum BusyEnterBehavior
{
    /// <summary>Enter queues; Ctrl+Enter steers.</summary>
    Queue,

    /// <summary>Enter steers; Ctrl+Enter queues.</summary>
    Steer,
}

/// <summary>
/// The message box, and the rules about what typing into it does.
/// </summary>
/// <remarks>
/// Queueing and steering are different acts and the composer keeps them distinct: a
/// queued message opens a turn of its own, while steering joins the turn already
/// running. Which key does which is a preference, because people disagree about it
/// and both are reasonable defaults.
///
/// The composer stays disabled until a workspace is chosen. An agent with no
/// workspace has nowhere to read or write, so accepting a prompt would only produce
/// a confusing failure.
/// </remarks>
public sealed partial class ComposerViewModel : ObservableObject
{
    private readonly Func<IAgent?> _agent;

    [ObservableProperty]
    private string _draft = string.Empty;

    [ObservableProperty]
    private bool _hasWorkspace;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isBlocked;

    [ObservableProperty]
    private string? _blockedReason;

    [ObservableProperty]
    private BusyEnterBehavior _busyEnter = BusyEnterBehavior.Queue;

    /// <param name="agent">Reads the agent to send to, which changes as sessions do.</param>
    public ComposerViewModel(Func<IAgent?> agent) => _agent = agent;

    /// <summary>Whether the composer accepts input at all.</summary>
    public bool IsEnabled => HasWorkspace && !IsBlocked && _agent() is not null;

    /// <summary>What a view should show when the composer is disabled.</summary>
    public string Placeholder => !HasWorkspace
        ? "Choose a workspace to begin."
        : IsBlocked
            ? BlockedReason ?? "Not available right now."
            : IsRunning
                ? "Queue another message, or steer the running turn."
                : "Ask for something.";

    /// <summary>Messages waiting to open a turn of their own.</summary>
    public IReadOnlyList<QueuedMessage> Queued => Rows(_agent()?.Inbox.NextTurn);

    /// <summary>Messages waiting to join the running turn.</summary>
    public IReadOnlyList<QueuedMessage> Steering => Rows(_agent()?.Inbox.NextStep);

    /// <summary>Whether anything is waiting, which is what shows the queue dock.</summary>
    public bool HasQueue => Queued.Count > 0;

    /// <summary>
    /// Re-read the inbox after it changed.
    /// </summary>
    /// <remarks>
    /// Driven by the durable <c>agent/inbox/spliced</c> event rather than by the live
    /// lists, so the dock is a projection of the log like everything else on screen and
    /// a reopened session shows the queue it really has.
    /// </remarks>
    public void NotifyQueueChanged()
    {
        OnPropertyChanged(nameof(Queued));
        OnPropertyChanged(nameof(Steering));
        OnPropertyChanged(nameof(HasQueue));
    }

    private static IReadOnlyList<QueuedMessage> Rows(IReadOnlyList<Message>? messages) => messages is null
        ? []
        : [.. messages.Select(static message =>
            new QueuedMessage(message.Id, ContentBlocks.FlattenText(message.Content)))];

    /// <summary>
    /// Decide what a key press means.
    /// </summary>
    /// <param name="shift">Whether shift was held.</param>
    /// <param name="control">Whether control was held.</param>
    /// <returns>The action to take.</returns>
    public ComposerAction ResolveEnter(bool shift, bool control)
    {
        if (shift) return ComposerAction.Newline;
        if (!IsEnabled || string.IsNullOrWhiteSpace(Draft)) return ComposerAction.None;

        // Steering only makes sense while something is running; otherwise both keys
        // mean the same thing and the distinction would only confuse.
        if (!IsRunning) return ComposerAction.Queue;

        var primary = BusyEnter == BusyEnterBehavior.Queue ? ComposerAction.Queue : ComposerAction.Steer;
        var secondary = primary == ComposerAction.Queue ? ComposerAction.Steer : ComposerAction.Queue;
        return control ? secondary : primary;
    }

    /// <summary>
    /// Send the draft.
    /// </summary>
    /// <param name="action">Whether to queue it or steer with it.</param>
    /// <returns>True when something was sent.</returns>
    public bool Submit(ComposerAction action)
    {
        if (action is not (ComposerAction.Queue or ComposerAction.Steer)) return false;
        if (!IsEnabled || string.IsNullOrWhiteSpace(Draft)) return false;
        if (_agent() is not { } agent) return false;

        var message = Message.UserText(Draft.TrimEnd());
        if (action == ComposerAction.Queue) agent.Followup(message);
        else agent.Steer(message);

        Draft = string.Empty;
        return true;
    }

    /// <summary>
    /// Stop whatever is running.
    /// </summary>
    /// <remarks>Queued work is discarded too, which is what a person pressing stop means.</remarks>
    public void Stop() => _agent()?.Cancel(UserCancel.Instance);

    /// <summary>
    /// Remove one queued message.
    /// </summary>
    /// <param name="id">The message to remove.</param>
    /// <returns>True when it was found.</returns>
    public bool Remove(MessageId id) => _agent()?.Inbox.Remove(id) ?? false;

    /// <summary>
    /// Replace one queued message in place.
    /// </summary>
    /// <param name="id">The message to replace.</param>
    /// <param name="text">Its new text.</param>
    /// <returns>True when it was found.</returns>
    public bool Edit(MessageId id, string text)
        => _agent()?.Inbox.Replace(id, Message.UserText(text)) ?? false;

    /// <summary>
    /// Move one queued message into the running turn.
    /// </summary>
    /// <param name="id">The message to promote.</param>
    /// <returns>True when it was found.</returns>
    public bool Promote(MessageId id) => _agent()?.Inbox.Steer(id) ?? false;

    /// <inheritdoc />
    partial void OnHasWorkspaceChanged(bool value) => NotifyReadiness();

    /// <inheritdoc />
    partial void OnIsBlockedChanged(bool value) => NotifyReadiness();

    /// <inheritdoc />
    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(Placeholder));

    private void NotifyReadiness()
    {
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(Placeholder));
    }
}

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.Agent;
using Dsh.Bundle.Base;
using Dsh.Cordis;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Tools;

namespace Dsh.App.Core;

/// <summary>
/// One open conversation.
/// </summary>
/// <remarks>
/// Everything shown here is projected from the session log, and every update arrives
/// through the same listener whether it came from a live turn or from replaying a
/// stored session. That is what makes the view honest: there is no second source of
/// truth it could drift from.
/// </remarks>
public sealed partial class SessionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Harness _harness;
    private readonly AgentHandle _handle;
    private readonly IDisposable _events;
    private readonly IDisposable _status;
    private readonly Func<Action, Task> _toUiThread;

    [ObservableProperty]
    private string _title = "New session";

    [ObservableProperty]
    private bool _isRunning;

    private SessionViewModel(Harness harness, AgentHandle handle, Func<Action, Task> toUiThread)
    {
        _harness = harness;
        _handle = handle;
        _toUiThread = toUiThread;

        Conversation = new ConversationProjection(name => harness.Tools.View(handle.Agent.Scope).GetValueOrDefault(name));
        Composer = new ComposerViewModel(() => handle.Agent);

        Conversation.Replay(handle.Agent.Session.Events);
        RefreshTitle();

        _events = harness.Ctx.On(SessionKeys.Event, notice =>
        {
            if (!ReferenceEquals(notice.Session, handle.Agent.Session)) return;
            _ = _toUiThread(() =>
            {
                Conversation.Apply(notice.Event);
                if (string.Equals(notice.Event.Type, Inbox.Spliced.Name, StringComparison.Ordinal))
                {
                    Composer.NotifyQueueChanged();
                }

                RefreshTitle();
            });
        });

        _status = harness.Ctx.WithScope(handle.Agent.Scope).On(AgentKeys.Status, notice =>
        {
            if (!ReferenceEquals(notice.Agent, handle.Agent)) return;
            _ = _toUiThread(() =>
            {
                IsRunning = notice.Status == AgentStatus.Running;
                Composer.IsRunning = IsRunning;
            });
        });
    }

    /// <summary>The agent driving this conversation.</summary>
    public IAgent Agent => _handle.Agent;

    /// <summary>The durable log behind it.</summary>
    public Dsh.Session.Session Session => _handle.Agent.Session;

    /// <summary>The rows a view shows.</summary>
    public ConversationProjection Conversation { get; }

    /// <summary>The message box.</summary>
    public ComposerViewModel Composer { get; }

    /// <summary>The workspace this conversation belongs to.</summary>
    public string? Workspace => Session.Header.Cwd;

    /// <summary>The permission preset in force.</summary>
    public string Preset => _harness.Permissions.CurrentPreset();

    /// <summary>The presets that can be switched to, in composition order.</summary>
    public IReadOnlyList<string> PresetNames => [.. _harness.Permissions.Presets.Keys];

    /// <summary>
    /// Open a conversation over a new session.
    /// </summary>
    /// <param name="harness">The running harness.</param>
    /// <param name="toUiThread">Marshals updates onto the view's thread.</param>
    /// <returns>The conversation.</returns>
    public static async Task<SessionViewModel> CreateAsync(Harness harness, Func<Action, Task>? toUiThread = null)
    {
        var handle = await harness.CreateAgentAsync();
        return new SessionViewModel(harness, handle, toUiThread ?? Inline);
    }

    /// <summary>
    /// Reopen a stored conversation.
    /// </summary>
    /// <param name="harness">The running harness.</param>
    /// <param name="logPath">The stored log.</param>
    /// <param name="toUiThread">Marshals updates onto the view's thread.</param>
    /// <returns>The conversation, with its history already on screen.</returns>
    public static async Task<SessionViewModel> ResumeAsync(
        Harness harness,
        string logPath,
        Func<Action, Task>? toUiThread = null)
    {
        var handle = await harness.ResumeAgentAsync(logPath);
        return new SessionViewModel(harness, handle, toUiThread ?? Inline);
    }

    /// <summary>
    /// Change the permission preset for this conversation.
    /// </summary>
    /// <param name="preset">The preset to switch to.</param>
    public void SelectPreset(string preset)
    {
        // Recorded against the session's own log, so reopening it restores the settings
        // it was running under rather than whatever the app currently defaults to.
        AgentRegistry.WithInitiatorAsync(Agent, () =>
        {
            _harness.Permissions.SelectPreset(preset);
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();

        OnPropertyChanged(nameof(Preset));
    }

    /// <summary>
    /// Derive the conversation's name from its first prompt.
    /// </summary>
    /// <remarks>
    /// A cheap fallback rather than a model call: it costs nothing, it is available the
    /// instant someone types, and a session list is much more useful with imperfect
    /// names than with none.
    /// </remarks>
    private void RefreshTitle()
    {
        if (!string.Equals(Title, "New session", StringComparison.Ordinal)) return;

        foreach (var entry in Session.Events)
        {
            if (!string.Equals(entry.Type, "user/message", StringComparison.Ordinal)) continue;
            var message = entry.DataAs<Message>();
            if (message.Source is not UserMessageSource) continue;

            Title = SummarizeTitle(ContentBlocks.FlattenText(message.Content));
            return;
        }
    }

    /// <summary>
    /// Shorten a prompt into a name.
    /// </summary>
    /// <param name="prompt">The first prompt.</param>
    /// <param name="maxWords">How many words to keep.</param>
    /// <returns>The name, ellipsized when the prompt was longer.</returns>
    public static string SummarizeTitle(string prompt, int maxWords = 6)
    {
        var words = prompt.ReplaceLineEndings(" ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (words.Length == 0) return "New session";
        var kept = string.Join(' ', words.Take(maxWords));
        return words.Length > maxWords ? kept + "…" : kept;
    }

    private static Task Inline(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _events.Dispose();
        _status.Dispose();
        await _handle.DisposeAsync();
    }
}

/// <summary>One conversation in the sidebar, read without opening its whole log.</summary>
/// <param name="Id">Its session id.</param>
/// <param name="Title">Its name.</param>
/// <param name="Workspace">The directory it belongs to.</param>
/// <param name="Path">Where its log lives.</param>
/// <param name="UpdatedAt">When it last changed.</param>
public sealed record StoredSessionSummary(
    SessionId Id,
    string Title,
    string? Workspace,
    string Path,
    DateTimeOffset UpdatedAt)
{
    /// <summary>When it last changed, in the reader's own time zone and format.</summary>
    public string UpdatedLabel => UpdatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}

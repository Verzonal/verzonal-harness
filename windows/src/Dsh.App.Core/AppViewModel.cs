using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.Bundle.Base;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Session.Persistence;
using Dsh.Settings;

namespace Dsh.App.Core;

/// <summary>What the app looks like when it starts.</summary>
public enum AppTheme
{
    /// <summary>Follow the operating system.</summary>
    System,

    /// <summary>Always light.</summary>
    Light,

    /// <summary>Always dark.</summary>
    Dark,
}

/// <summary>
/// The application: one harness, the conversations open against it, and the settings
/// that shape both.
/// </summary>
/// <remarks>
/// The harness is composed per workspace, because the workspace decides what the
/// filesystem and shell providers point at and what writes are confined to. Choosing
/// a different workspace therefore rebuilds the composition rather than mutating it,
/// which keeps every capability's view of the world consistent.
/// </remarks>
public sealed partial class AppViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Func<Action, Task> _toUiThread;

    [ObservableProperty]
    private Harness? _harness;

    [ObservableProperty]
    private SessionViewModel? _current;

    [ObservableProperty]
    private ApprovalViewModel? _approval;

    [ObservableProperty]
    private string? _workspace;

    [ObservableProperty]
    private AppTheme _theme = AppTheme.System;

    [ObservableProperty]
    private string? _startupError;

    /// <param name="toUiThread">Marshals updates onto the view's thread.</param>
    public AppViewModel(Func<Action, Task>? toUiThread = null)
        => _toUiThread = toUiThread ?? (action =>
        {
            action();
            return Task.CompletedTask;
        });

    /// <summary>Conversations already stored on disk, newest first.</summary>
    public ObservableCollection<StoredSessionSummary> StoredSessions { get; } = [];

    /// <summary>Conversations open in this run.</summary>
    public ObservableCollection<SessionViewModel> OpenSessions { get; } = [];

    /// <summary>The mounted plugins, as the settings page lists them.</summary>
    public IReadOnlyList<CompositionRow> Composition => Harness?.Rows ?? [];

    /// <summary>The permission presets a person can choose between.</summary>
    public IReadOnlyDictionary<string, PermissionPreset> Presets =>
        Harness?.Permissions.Presets ?? new Dictionary<string, PermissionPreset>(StringComparer.Ordinal);

    /// <summary>Whether a model route can actually be called.</summary>
    public bool HasCredential => Harness?.Credentials.Describe("DEEPSEEK_API_KEY").Configured ?? false;

    /// <summary>
    /// Point the app at a workspace, composing a harness for it.
    /// </summary>
    /// <param name="workspace">The directory to work in.</param>
    /// <param name="options">Composition overrides, for tests and alternate providers.</param>
    /// <returns>A task completing once the harness is up and a conversation is open.</returns>
    public async Task OpenWorkspaceAsync(string workspace, HarnessOptions? options = null)
    {
        await CloseHarnessAsync();
        StartupError = null;

        try
        {
            var resolved = options ?? new HarnessOptions(workspace);
            Harness = await Harness.StartAsync(resolved with { Workspace = workspace });
            Workspace = Path.GetFullPath(workspace);
            Approval = new ApprovalViewModel(Harness.Ctx, _toUiThread);

            LoadTheme();
            RefreshStoredSessions();
            await NewSessionAsync();
        }
        catch (Exception error)
        {
            // A failure here is the whole app failing to start, so it is surfaced rather
            // than logged: there is nothing else for the person to look at.
            StartupError = Cordis.ErrorChain.Describe(error);
        }

        OnPropertyChanged(nameof(Composition));
        OnPropertyChanged(nameof(Presets));
        OnPropertyChanged(nameof(HasCredential));
    }

    /// <summary>
    /// Open a new conversation in the current workspace.
    /// </summary>
    /// <returns>The conversation.</returns>
    /// <exception cref="InvalidOperationException">No workspace has been chosen.</exception>
    public async Task<SessionViewModel> NewSessionAsync()
    {
        var harness = Harness ?? throw new InvalidOperationException("choose a workspace first");
        var session = await SessionViewModel.CreateAsync(harness, _toUiThread);
        session.Composer.HasWorkspace = true;

        OpenSessions.Add(session);
        Current = session;
        return session;
    }

    /// <summary>
    /// Reopen a stored conversation.
    /// </summary>
    /// <param name="stored">The conversation to reopen.</param>
    /// <returns>The conversation, with its history already on screen.</returns>
    /// <exception cref="InvalidOperationException">No workspace has been chosen.</exception>
    public async Task<SessionViewModel> ResumeAsync(StoredSessionSummary stored)
    {
        var harness = Harness ?? throw new InvalidOperationException("choose a workspace first");

        var existing = OpenSessions.FirstOrDefault(session => session.Session.Id == stored.Id);
        if (existing is not null)
        {
            Current = existing;
            return existing;
        }

        var session = await SessionViewModel.ResumeAsync(harness, stored.Path, _toUiThread);
        session.Composer.HasWorkspace = true;

        OpenSessions.Add(session);
        Current = session;
        return session;
    }

    /// <summary>Re-read the stored conversation list.</summary>
    public void RefreshStoredSessions()
    {
        StoredSessions.Clear();
        if (Harness?.Persistence is not { } persistence) return;

        foreach (var stored in persistence.List())
        {
            StoredSessions.Add(new StoredSessionSummary(
                stored.Header.Id,
                TitleOf(stored),
                stored.Header.Cwd,
                stored.Path,
                stored.UpdatedAt));
        }
    }

    /// <summary>
    /// Save the API key a person entered.
    /// </summary>
    /// <param name="key">The key.</param>
    public void SaveCredential(string key)
    {
        Harness?.Credentials.Set("DEEPSEEK_API_KEY", key);
        OnPropertyChanged(nameof(HasCredential));
    }

    /// <summary>
    /// Remember which theme a person chose.
    /// </summary>
    /// <param name="theme">The theme.</param>
    public void SaveTheme(AppTheme theme)
    {
        Theme = theme;
        Harness?.Settings.Update(
            "ui",
            new Dictionary<string, object?> { ["theme"] = theme.ToString().ToLowerInvariant() });
    }

    private void LoadTheme()
    {
        var stored = Harness?.Settings.Get("ui", "theme", "system") ?? "system";
        Theme = stored switch
        {
            "light" => AppTheme.Light,
            "dark" => AppTheme.Dark,
            _ => AppTheme.System,
        };
    }

    /// <summary>
    /// Name a stored conversation from its first prompt.
    /// </summary>
    /// <param name="stored">The stored conversation.</param>
    /// <returns>Its name, or a fallback when nothing was said in it.</returns>
    /// <remarks>
    /// Reads the log rather than trusting a cached name, so a list entry always
    /// describes what is actually in the file.
    /// </remarks>
    internal static string TitleOf(StoredSession stored)
    {
        try
        {
            var (_, events) = JsonlPersistence.Read(stored.Path);
            foreach (var entry in events)
            {
                if (!string.Equals(entry.Type, "user/message", StringComparison.Ordinal)) continue;
                var message = entry.DataAs<Message>();
                if (message.Source is not UserMessageSource) continue;
                return SessionViewModel.SummarizeTitle(ContentBlocks.FlattenText(message.Content));
            }
        }
        catch (Exception)
        {
            // A log that cannot be read still deserves a row in the list, so the person
            // can see it exists rather than wondering where it went.
        }

        return "Untitled session";
    }

    private async Task CloseHarnessAsync()
    {
        foreach (var session in OpenSessions) await session.DisposeAsync();
        OpenSessions.Clear();
        Current = null;

        Approval?.Dispose();
        Approval = null;

        if (Harness is { } harness) await harness.DisposeAsync();
        Harness = null;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(CloseHarnessAsync());
}

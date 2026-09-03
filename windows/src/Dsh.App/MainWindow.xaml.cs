using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Dsh.App.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Dsh.App;

/// <summary>
/// The window: a session list, one conversation at a time, and settings.
/// </summary>
/// <remarks>
/// Holds no state of its own beyond which pane is showing. Everything else is read
/// from <see cref="AppViewModel" />, which is where the behavior is tested.
/// </remarks>
public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private bool _switchingSession;

    /// <summary>Initialize the window.</summary>
    public MainWindow()
    {
        InitializeComponent();

        Title = "DeepSeek Harness";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        ViewModel = new AppViewModel(ToUiThread);
        ViewModel.PropertyChanged += OnAppChanged;

        Settings.App = ViewModel;
        Closed += async (_, _) => await ViewModel.DisposeAsync();
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The application state this window shows.</summary>
    public AppViewModel ViewModel { get; }

    /// <summary>Whether a workspace has been chosen, which gates everything else.</summary>
    public bool HasWorkspace => ViewModel.Workspace is not null;

    /// <summary>
    /// Run work on the thread that owns the window.
    /// </summary>
    /// <param name="action">The work.</param>
    /// <returns>A task completing once it has run.</returns>
    /// <remarks>
    /// Turns arrive on whichever thread the agent loop is running on, so every update
    /// crosses here before it touches an element. A queue that refuses the work — the
    /// window is closing — completes the task rather than leaving a caller waiting on a
    /// thread that will never run it.
    /// </remarks>
    private Task ToUiThread(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _dispatcher.TryEnqueue(() =>
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        });

        if (!queued) completion.TrySetResult();
        return completion.Task;
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(AppViewModel.Current):
                Conversation.Session = ViewModel.Current;
                Composer.Session = ViewModel.Current;
                SelectCurrentInPane();
                break;

            case nameof(AppViewModel.Approval):
                Composer.Approval = ViewModel.Approval;
                break;

            case nameof(AppViewModel.Workspace):
                Raise(nameof(HasWorkspace));
                break;

            case nameof(AppViewModel.Theme):
                ApplyTheme();
                break;

            default:
                break;
        }
    }

    private void ApplyTheme()
    {
        if (Root is not FrameworkElement root) return;

        root.RequestedTheme = ViewModel.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    private void SelectCurrentInPane()
    {
        _switchingSession = true;
        try
        {
            Nav.SelectedItem = ViewModel.Current;
        }
        finally
        {
            _switchingSession = false;
        }
    }

    private async void OnChooseWorkspace(object sender, RoutedEventArgs args)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");

        // An unpackaged app has no implicit window for the dialog to belong to, so it is
        // given one; without this the picker throws rather than opening.
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(this));

        if (await picker.PickSingleFolderAsync() is not { } folder) return;
        await ViewModel.OpenWorkspaceAsync(folder.Path);
    }

    private async void OnNewSession(object sender, RoutedEventArgs args)
    {
        if (ViewModel.Harness is null) return;
        await ViewModel.NewSessionAsync();
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_switchingSession) return;

        var settings = args.IsSettingsSelected;
        Settings.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        ChatPane.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        WelcomePane.Visibility = settings || HasWorkspace ? Visibility.Collapsed : Visibility.Visible;

        if (settings)
        {
            Settings.App = ViewModel;
            return;
        }

        if (args.SelectedItem is SessionViewModel session) ViewModel.Current = session;
    }

    private async void OnStoredSessionChosen(object sender, SelectionChangedEventArgs args)
    {
        if (args.AddedItems.Count == 0) return;
        if (args.AddedItems[0] is not StoredSessionSummary stored) return;

        // The list is a way in, not a place to sit: clearing the selection means picking
        // the same session again reopens it instead of doing nothing.
        if (sender is ListView list) list.SelectedItem = null;

        await ViewModel.ResumeAsync(stored);
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

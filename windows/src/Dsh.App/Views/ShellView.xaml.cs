using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Dsh.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Dsh.App.Views;

/// <summary>
/// Everything inside the window: the session sidebar, one conversation at a time,
/// and settings.
/// </summary>
/// <remarks>
/// A control rather than the window itself because compiled bindings need a
/// <see cref="FrameworkElement" /> to root them, and a WinUI
/// <see cref="Microsoft.UI.Xaml.Window" /> is not one.
///
/// It holds no state beyond which pane is showing; everything else is read from
/// <see cref="AppViewModel" />, which is where the behavior is tested.
/// </remarks>
public sealed partial class ShellView : UserControl, INotifyPropertyChanged
{
    private AppViewModel? _viewModel;
    private bool _switchingSession;

    /// <summary>Initialize the view.</summary>
    public ShellView() => InitializeComponent();

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when a person asks to pick a workspace, which needs the window.</summary>
    public event EventHandler? ChooseWorkspaceRequested;

    /// <summary>The strip the window uses as its draggable title bar.</summary>
    public FrameworkElement TitleBar => TitleBarArea;

    /// <summary>The application state this view shows.</summary>
    public AppViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnAppChanged;
            _viewModel = value;
            if (_viewModel is not null) _viewModel.PropertyChanged += OnAppChanged;

            SettingsPane.App = value;
            Raise(nameof(ViewModel));
            Raise(nameof(HasWorkspace));
        }
    }

    /// <summary>Whether a workspace has been chosen, which gates everything else.</summary>
    public bool HasWorkspace => ViewModel?.Workspace is not null;

    private void OnAppChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(AppViewModel.Current):
                Conversation.Session = ViewModel?.Current;
                Composer.Session = ViewModel?.Current;
                SelectCurrentInPane();
                break;

            case nameof(AppViewModel.Approval):
                Composer.Approval = ViewModel?.Approval;
                break;

            case nameof(AppViewModel.Workspace):
                Raise(nameof(HasWorkspace));
                break;

            case nameof(AppViewModel.Theme):
                RequestedTheme = ViewModel?.Theme switch
                {
                    AppTheme.Light => ElementTheme.Light,
                    AppTheme.Dark => ElementTheme.Dark,
                    _ => ElementTheme.Default,
                };
                break;

            default:
                break;
        }
    }

    private void SelectCurrentInPane()
    {
        _switchingSession = true;
        try
        {
            Nav.SelectedItem = ViewModel?.Current;
        }
        finally
        {
            _switchingSession = false;
        }
    }

    private void OnChooseWorkspace(object sender, RoutedEventArgs args)
        => ChooseWorkspaceRequested?.Invoke(this, EventArgs.Empty);

    private async void OnNewSession(object sender, RoutedEventArgs args)
    {
        if (ViewModel?.Harness is null) return;
        await ViewModel.NewSessionAsync();
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_switchingSession) return;

        var settings = args.IsSettingsSelected;
        SettingsPane.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        ChatPane.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        WelcomePane.Visibility = settings || HasWorkspace ? Visibility.Collapsed : Visibility.Visible;

        if (settings)
        {
            SettingsPane.App = ViewModel;
            return;
        }

        if (args.SelectedItem is SessionViewModel session && ViewModel is { } app) app.Current = session;
    }

    private async void OnStoredSessionChosen(object sender, SelectionChangedEventArgs args)
    {
        if (args.AddedItems.Count == 0) return;
        if (args.AddedItems[0] is not StoredSessionSummary stored) return;

        // The list is a way in, not a place to sit: clearing the selection means picking
        // the same session again reopens it instead of doing nothing.
        if (sender is ListView list) list.SelectedItem = null;

        if (ViewModel is { } app) await app.ResumeAsync(stored);
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

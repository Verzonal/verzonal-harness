using System;
using System.Threading.Tasks;
using Dsh.App.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace Dsh.App;

/// <summary>
/// The window.
/// </summary>
/// <remarks>
/// Owns three things a control cannot: the title bar, the dispatcher every update is
/// marshalled through, and the handle a folder picker has to belong to. Everything
/// else is in <see cref="Views.ShellView" />.
/// </remarks>
public sealed partial class MainWindow : Window
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    /// <summary>Initialize the window.</summary>
    public MainWindow()
    {
        InitializeComponent();

        Title = "DeepSeek Harness";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(Shell.TitleBar);

        ViewModel = new AppViewModel(ToUiThread);
        Shell.ViewModel = ViewModel;
        Shell.ChooseWorkspaceRequested += async (_, _) => await ChooseWorkspaceAsync();

        Closed += async (_, _) => await ViewModel.DisposeAsync();
    }

    /// <summary>The application state this window shows.</summary>
    public AppViewModel ViewModel { get; }

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

    private async Task ChooseWorkspaceAsync()
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
}

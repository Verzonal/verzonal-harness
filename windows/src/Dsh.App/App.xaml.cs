using Microsoft.UI.Xaml;

namespace Dsh.App;

/// <summary>
/// The application object.
/// </summary>
/// <remarks>
/// Deliberately almost empty. Everything the app knows how to do lives in
/// <c>Dsh.App.Core</c>, which is unit-tested; this project only puts pixels on screen.
/// </remarks>
public partial class App : Application
{
    private Window? _window;

    /// <summary>Initialize the application object.</summary>
    public App() => InitializeComponent();

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}

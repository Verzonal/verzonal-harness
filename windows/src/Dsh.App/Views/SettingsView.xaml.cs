using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Dsh.App.Core;
using Dsh.Bundle.Base;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Dsh.App.Views;

/// <summary>
/// Settings, including the live plugin list.
/// </summary>
/// <remarks>
/// The plugin list is the point of this page as much as the key box is: it is the
/// desktop equivalent of dumping the composition, and it is what makes "everything is
/// a plugin" something a person can see rather than something the docs assert.
/// </remarks>
public sealed partial class SettingsView : UserControl, INotifyPropertyChanged
{
    private AppViewModel? _app;
    private bool _loading;

    /// <summary>Initialize the view.</summary>
    public SettingsView() => InitializeComponent();

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The application whose settings these are.</summary>
    public AppViewModel? App
    {
        get => _app;
        set
        {
            _app = value;
            Load();
            Raise(nameof(App));
            Raise(nameof(Composition));
            Raise(nameof(PresetNames));
            Raise(nameof(CredentialState));
        }
    }

    /// <summary>The mounted plugins.</summary>
    public IReadOnlyList<CompositionRow> Composition => App?.Composition ?? [];

    /// <summary>The permission presets to choose between.</summary>
    public IReadOnlyList<string> PresetNames => App is null ? [] : [.. App.Presets.Keys];

    /// <summary>Whether a model route can actually be called.</summary>
    public string CredentialState => App?.HasCredential == true
        ? "A key is configured."
        : "No key configured — the model cannot be called.";

    /// <summary>
    /// Show what is currently stored.
    /// </summary>
    /// <remarks>
    /// The guard stops the selection changes this makes from being read back as a
    /// person's choice and written straight to disk.
    /// </remarks>
    private void Load()
    {
        if (App is not { } app) return;

        _loading = true;
        try
        {
            ThemeChoice.SelectedIndex = app.Theme switch
            {
                AppTheme.Light => 1,
                AppTheme.Dark => 2,
                _ => 0,
            };

            PresetChoice.SelectedItem = app.Current?.Preset;
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnSaveKey(object sender, RoutedEventArgs args)
    {
        if (App is not { } app) return;
        if (string.IsNullOrWhiteSpace(ApiKey.Password)) return;

        app.SaveCredential(ApiKey.Password.Trim());

        // Cleared immediately: the box has done its job, and a key left on screen is a
        // key waiting to be read over a shoulder or caught in a screenshot.
        ApiKey.Password = string.Empty;
        Raise(nameof(CredentialState));
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || App is not { } app) return;

        app.SaveTheme(ThemeChoice.SelectedIndex switch
        {
            1 => AppTheme.Light,
            2 => AppTheme.Dark,
            _ => AppTheme.System,
        });
    }

    private void OnPresetChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || App?.Current is not { } session) return;
        if (PresetChoice.SelectedItem is string preset) session.SelectPreset(preset);
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

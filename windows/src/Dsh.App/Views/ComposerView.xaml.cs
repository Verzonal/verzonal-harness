using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Dsh.App.Core;
using Dsh.Llm;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace Dsh.App.Views;

/// <summary>
/// The message box, the docks under it, and the approval takeover that replaces it.
/// </summary>
/// <remarks>
/// Every rule about what a key press does lives in <see cref="ComposerViewModel" />
/// and is unit-tested there; this class only reports which keys were down and carries
/// out the answer.
/// </remarks>
public sealed partial class ComposerView : UserControl, INotifyPropertyChanged
{
    private SessionViewModel? _session;
    private ApprovalViewModel? _approval;

    /// <summary>Initialize the view.</summary>
    public ComposerView() => InitializeComponent();

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The conversation this composer sends to.</summary>
    public SessionViewModel? Session
    {
        get => _session;
        set
        {
            if (ReferenceEquals(_session, value)) return;

            if (_session is not null) _session.Composer.PropertyChanged -= OnComposerChanged;
            _session = value;
            if (_session is not null) _session.Composer.PropertyChanged += OnComposerChanged;

            Raise(nameof(Session));
            Raise(nameof(Queued));
            Raise(nameof(HasQueue));
            Raise(nameof(Preset));
        }
    }

    /// <summary>The approval channel, whose pending question takes the composer over.</summary>
    public ApprovalViewModel? Approval
    {
        get => _approval;
        set
        {
            if (ReferenceEquals(_approval, value)) return;

            if (_approval is not null) _approval.PropertyChanged -= OnApprovalChanged;
            _approval = value;
            if (_approval is not null) _approval.PropertyChanged += OnApprovalChanged;

            Raise(nameof(Approval));
            Raise(nameof(ApprovalMessage));
        }
    }

    /// <summary>Messages waiting their turn.</summary>
    public IReadOnlyList<QueuedMessage> Queued => Session?.Composer.Queued ?? [];

    /// <summary>Whether the queue dock has anything to show.</summary>
    public bool HasQueue => Queued.Count > 0;

    /// <summary>The permission preset in force, shown on the chip.</summary>
    public string Preset => Session?.Preset ?? string.Empty;

    /// <summary>What the pending question is asking, in the model's own words.</summary>
    public string ApprovalMessage => Approval?.Question is { } question
        ? string.IsNullOrWhiteSpace(question.Reason)
            ? $"{question.ToolName} needs permission to run."
            : $"{question.ToolName}: {question.Reason}"
        : string.Empty;

    private void OnComposerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ComposerViewModel.Queued) or nameof(ComposerViewModel.HasQueue))
        {
            Raise(nameof(Queued));
            Raise(nameof(HasQueue));
        }
    }

    private void OnApprovalChanged(object? sender, PropertyChangedEventArgs args)
        => Raise(nameof(ApprovalMessage));

    private void OnDraftKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter) return;
        if (Session is not { } session) return;

        var action = session.Composer.ResolveEnter(IsDown(VirtualKey.Shift), IsDown(VirtualKey.Control));

        // A newline is the text box's own job; anything else is ours, and marking it
        // handled is what stops the key from also inserting a line break.
        if (action == ComposerAction.Newline) return;

        args.Handled = true;
        session.Composer.Submit(action);
    }

    private void OnSend(object sender, RoutedEventArgs args)
    {
        if (Session is not { } session) return;
        session.Composer.Submit(session.Composer.ResolveEnter(shift: false, control: false));
    }

    private void OnStop(object sender, RoutedEventArgs args) => Session?.Composer.Stop();

    private void OnAllowOnce(object sender, RoutedEventArgs args) => Approval?.AllowOnce();

    private void OnReject(object sender, RoutedEventArgs args) => Approval?.Reject();

    private void OnRemoveQueued(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: MessageId id }) Session?.Composer.Remove(id);
    }

    /// <summary>
    /// Step to the next permission preset.
    /// </summary>
    /// <remarks>
    /// A chip that cycles rather than a menu: there are three presets, and the one in
    /// force is always the label, so a person can see and change it in one place.
    /// </remarks>
    private void OnCyclePreset(object sender, RoutedEventArgs args)
    {
        if (Session is not { } session) return;

        var names = session.PresetNames;
        if (names.Count == 0) return;

        var index = names.ToList().IndexOf(session.Preset);
        session.SelectPreset(names[(index + 1) % names.Count]);
        Raise(nameof(Preset));
    }

    private static bool IsDown(VirtualKey key)
        => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

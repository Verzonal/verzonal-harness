using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Dsh.App.Core;
using Microsoft.UI.Xaml.Controls;

namespace Dsh.App.Views;

/// <summary>
/// The conversation, drawn from the session log and nothing else.
/// </summary>
/// <remarks>
/// Follows the tail while a person is already at the bottom and stops following the
/// moment they scroll up, because yanking the view away from something being read is
/// worse than missing the newest line.
/// </remarks>
public sealed partial class ConversationView : UserControl, INotifyPropertyChanged
{
    private SessionViewModel? _session;
    private INotifyCollectionChanged? _watching;
    private bool _followTail = true;

    /// <summary>Initialize the view.</summary>
    public ConversationView()
    {
        InitializeComponent();
        Rows.Loaded += (_, _) => Follow();
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The conversation being shown.</summary>
    public SessionViewModel? Session
    {
        get => _session;
        set
        {
            if (ReferenceEquals(_session, value)) return;

            if (_watching is not null) _watching.CollectionChanged -= OnRowsChanged;

            _session = value;
            _watching = value?.Conversation.Nodes;
            if (_watching is not null) _watching.CollectionChanged += OnRowsChanged;

            _followTail = true;
            Raise(nameof(Session));
            Raise(nameof(IsEmpty));
            Follow();
        }
    }

    /// <summary>Whether there is nothing to show yet.</summary>
    public bool IsEmpty => Session is null || Session.Conversation.Nodes.Count == 0;

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        Raise(nameof(IsEmpty));
        if (_followTail) Follow();
    }

    private void Follow()
    {
        if (Session is not { } session) return;
        if (session.Conversation.Nodes.Count == 0) return;

        Rows.ScrollIntoView(session.Conversation.Nodes[^1]);
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

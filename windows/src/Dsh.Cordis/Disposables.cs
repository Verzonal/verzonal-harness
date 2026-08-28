namespace Dsh.Cordis;

/// <summary>
/// Runs one callback the first time it is disposed. Later disposals are no-ops,
/// so a registration handed to several owners unwinds exactly once.
/// </summary>
public sealed class ActionDisposable : IDisposable
{
    private Action? _action;

    /// <param name="action">The teardown to run on first disposal.</param>
    public ActionDisposable(Action action) => _action = action;

    /// <summary>Whether the callback has already run.</summary>
    public bool IsDisposed => Volatile.Read(ref _action) is null;

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();

    /// <summary>A disposer that does nothing, for a registration with no teardown.</summary>
    public static IDisposable Empty { get; } = new ActionDisposable(static () => { });
}

/// <summary>
/// An ordered set of disposers unwound in reverse registration order, so teardown
/// mirrors the order the effects were installed in.
/// </summary>
/// <remarks>
/// Disposal is contained: every entry runs even when an earlier one throws, and the
/// collected failures surface together as an <see cref="AggregateException" />.
/// </remarks>
public sealed class DisposableStack : IDisposable
{
    private readonly List<IDisposable> _entries = [];
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>How many entries are still registered.</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _entries.Count;
        }
    }

    /// <summary>Whether the stack has already unwound.</summary>
    public bool IsDisposed
    {
        get
        {
            lock (_gate) return _disposed;
        }
    }

    /// <summary>
    /// Add one disposer to the stack.
    /// </summary>
    /// <param name="disposable">The teardown to own.</param>
    /// <returns>A disposer that removes and runs just this entry, leaving the rest intact.</returns>
    /// <remarks>Adding to an already-unwound stack disposes the entry immediately.</remarks>
    public IDisposable Add(IDisposable disposable)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                disposable.Dispose();
                return ActionDisposable.Empty;
            }

            _entries.Add(disposable);
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                if (_disposed || !_entries.Remove(disposable)) return;
            }

            disposable.Dispose();
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        List<IDisposable> pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            pending = [.. _entries];
            _entries.Clear();
        }

        List<Exception>? failures = null;
        for (var index = pending.Count - 1; index >= 0; index--)
        {
            try
            {
                pending[index].Dispose();
            }
            catch (Exception error)
            {
                (failures ??= []).Add(error);
            }
        }

        if (failures is { Count: > 0 }) throw new AggregateException("effect teardown failed", failures);
    }
}

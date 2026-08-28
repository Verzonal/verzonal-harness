namespace Dsh.Cordis;

/// <summary>
/// Identity of one registration boundary that events can be filtered by, such as a
/// single agent. Scopes compare by reference, so two boundaries never collide.
/// </summary>
public sealed class ScopeKey
{
    /// <param name="label">Human-readable name used in diagnostics only.</param>
    public ScopeKey(string label) => Label = label;

    /// <summary>Diagnostic name; never used for comparison.</summary>
    public string Label { get; }

    /// <inheritdoc />
    public override string ToString() => $"scope({Label})";
}

/// <summary>
/// An event whose listeners observe a payload without returning anything and
/// without being awaited. Dispatched with <see cref="Context.Emit{TPayload}" />.
/// </summary>
/// <typeparam name="TPayload">The payload every listener receives.</typeparam>
/// <param name="Name">Stable event name, for example <c>session/event</c>.</param>
public sealed record EmitKey<TPayload>(string Name);

/// <summary>
/// An event whose listeners all run concurrently and are awaited together.
/// Dispatched with <see cref="Context.ParallelAsync{TPayload}" />.
/// </summary>
/// <typeparam name="TPayload">The payload every listener receives.</typeparam>
/// <param name="Name">Stable event name, for example <c>session/flush</c>.</param>
public sealed record ParallelKey<TPayload>(string Name);

/// <summary>
/// An event whose listeners run in registration order and are awaited one at a
/// time. Dispatched with <see cref="Context.SerialAsync{TPayload}" />.
/// </summary>
/// <typeparam name="TPayload">The payload every listener receives.</typeparam>
/// <param name="Name">Stable event name, for example <c>agent/turn-stopping</c>.</param>
public sealed record SerialKey<TPayload>(string Name);

/// <summary>
/// An around-middleware event: each listener receives the payload plus a
/// continuation, and either delegates through it or short-circuits by returning
/// without calling it. Dispatched with
/// <see cref="Context.WaterfallAsync{TPayload, TResult}" />.
/// </summary>
/// <typeparam name="TPayload">The payload every listener receives.</typeparam>
/// <typeparam name="TResult">The value composed through the chain.</typeparam>
/// <param name="Name">Stable event name, for example <c>agent/pre-step</c>.</param>
public sealed record WaterfallKey<TPayload, TResult>(string Name);

/// <summary>
/// One around-middleware listener. Call <paramref name="next" /> to delegate to the
/// rest of the chain and compose its result; return without calling it to own the
/// decision and short-circuit everything downstream.
/// </summary>
/// <typeparam name="TPayload">The dispatched payload.</typeparam>
/// <typeparam name="TResult">The value this chain composes.</typeparam>
/// <param name="payload">The dispatched payload.</param>
/// <param name="next">Runs the remainder of the chain, ending at the producer's default.</param>
/// <returns>The value this listener contributes to the chain.</returns>
public delegate Task<TResult> WaterfallListener<in TPayload, TResult>(TPayload payload, Func<Task<TResult>> next);

/// <summary>
/// The listener store behind every dispatch mode: ordered registrations per event
/// name, each optionally bound to one <see cref="ScopeKey" />.
/// </summary>
/// <remarks>
/// Dispatch always resolves a snapshot of the matching listeners before invoking
/// any of them, so a listener registered while an event is in flight does not
/// observe that event, and one disposed mid-dispatch still completes the round it
/// had already been selected for.
/// </remarks>
internal sealed class EventBus
{
    private sealed record Entry(object Listener, ScopeKey? Scope, long Order);

    private readonly Dictionary<string, List<Entry>> _entries = [];
    private readonly object _gate = new();
    private long _nextOrder;
    private long _nextPrependOrder = -1;

    /// <summary>
    /// Register one listener under an event name.
    /// </summary>
    /// <param name="name">The event name to listen on.</param>
    /// <param name="listener">The delegate, typed per dispatch mode.</param>
    /// <param name="scope">Bind the listener to one scope, or null to observe every dispatch.</param>
    /// <param name="prepend">Place the listener ahead of ordinary registrations.</param>
    /// <returns>A disposer removing exactly this registration.</returns>
    public IDisposable Add(string name, object listener, ScopeKey? scope, bool prepend)
    {
        Entry entry;
        lock (_gate)
        {
            var order = prepend ? _nextPrependOrder-- : _nextOrder++;
            entry = new Entry(listener, scope, order);
            if (!_entries.TryGetValue(name, out var list))
            {
                list = [];
                _entries[name] = list;
            }

            list.Add(entry);
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                if (!_entries.TryGetValue(name, out var list)) return;
                list.Remove(entry);
                if (list.Count == 0) _entries.Remove(name);
            }
        });
    }

    /// <summary>
    /// Snapshot the listeners that a dispatch on this name and scope must reach.
    /// </summary>
    /// <param name="name">The event name being dispatched.</param>
    /// <param name="scope">The dispatch's scope; unscoped listeners always match.</param>
    /// <returns>The matching listeners in registration order, prepended ones first.</returns>
    public IReadOnlyList<object> Snapshot(string name, ScopeKey? scope)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(name, out var list) || list.Count == 0) return [];
            var matched = new List<Entry>(list.Count);
            foreach (var entry in list)
            {
                if (entry.Scope is null || ReferenceEquals(entry.Scope, scope)) matched.Add(entry);
            }

            if (matched.Count == 0) return [];
            matched.Sort(static (left, right) => left.Order.CompareTo(right.Order));
            var result = new object[matched.Count];
            for (var index = 0; index < matched.Count; index++) result[index] = matched[index].Listener;
            return result;
        }
    }

    /// <summary>
    /// Count the registrations on one event name, ignoring scope.
    /// </summary>
    /// <param name="name">The event name to count.</param>
    /// <returns>How many listeners are registered.</returns>
    public int CountOf(string name)
    {
        lock (_gate) return _entries.TryGetValue(name, out var list) ? list.Count : 0;
    }
}

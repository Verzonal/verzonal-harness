using System.Diagnostics.CodeAnalysis;

namespace Dsh.Cordis;

/// <summary>
/// Shared state behind every <see cref="Context" /> in one tree: the service store,
/// the listener store, and the logger. One runtime is created by
/// <see cref="Context.CreateRoot" /> and reached by every descendant.
/// </summary>
internal sealed class CordisRuntime
{
    private readonly Dictionary<string, object> _services = [];
    private readonly List<Action<string>> _watchers = [];
    private readonly object _gate = new();

    public CordisRuntime(ILogger logger) => Logger = logger;

    public ILogger Logger { get; }

    public EventBus Events { get; } = new();

    public object? Get(string key)
    {
        lock (_gate) return _services.GetValueOrDefault(key);
    }

    public bool Has(string key)
    {
        lock (_gate) return _services.ContainsKey(key);
    }

    public IReadOnlyList<string> Keys
    {
        get
        {
            lock (_gate) return [.. _services.Keys];
        }
    }

    /// <summary>
    /// Publish one service under a key.
    /// </summary>
    /// <param name="key">The context key other plugins reach it by.</param>
    /// <param name="instance">The service instance.</param>
    /// <returns>A disposer that revokes the service.</returns>
    /// <exception cref="InvalidOperationException">The key is already claimed.</exception>
    public IDisposable Provide(string key, object instance)
    {
        lock (_gate)
        {
            if (_services.TryGetValue(key, out var existing))
            {
                throw new InvalidOperationException(
                    $"service \"{key}\" is already provided by {existing.GetType().Name}");
            }

            _services[key] = instance;
        }

        NotifyWatchers(key);

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                if (!_services.TryGetValue(key, out var current) || !ReferenceEquals(current, instance)) return;
                _services.Remove(key);
            }

            NotifyWatchers(key);
        });
    }

    /// <summary>
    /// Observe every service appearance and revocation.
    /// </summary>
    /// <param name="watcher">Receives the key whose availability changed.</param>
    /// <returns>A disposer removing the watcher.</returns>
    public IDisposable Watch(Action<string> watcher)
    {
        lock (_gate) _watchers.Add(watcher);
        return new ActionDisposable(() =>
        {
            lock (_gate) _watchers.Remove(watcher);
        });
    }

    private void NotifyWatchers(string key)
    {
        Action<string>[] snapshot;
        lock (_gate) snapshot = [.. _watchers];
        foreach (var watcher in snapshot)
        {
            try
            {
                watcher(key);
            }
            catch (Exception error)
            {
                Logger.Log(LogLevel.Warn, "cordis", $"service watcher for \"{key}\" failed", error);
            }
        }
    }
}

/// <summary>
/// A repository of services and the boundary every contribution is registered
/// against. Plugins find each other by context key rather than by importing a
/// concrete implementation, and every registration made through a context unwinds
/// when the owning plugin unloads.
/// </summary>
public sealed class Context
{
    private readonly CordisRuntime _runtime;
    private readonly IReadOnlyDictionary<string, object> _values;

    private Context(CordisRuntime runtime, Fiber? fiber, ScopeKey? scope, IReadOnlyDictionary<string, object> values)
    {
        _runtime = runtime;
        Fiber = fiber;
        Scope = scope;
        _values = values;
    }

    /// <summary>
    /// Create the root context of a new plugin tree.
    /// </summary>
    /// <param name="logger">Where contained failures are reported; discards them when omitted.</param>
    /// <returns>The root context, whose fiber owns registrations made directly on it.</returns>
    public static Context CreateRoot(ILogger? logger = null)
    {
        var runtime = new CordisRuntime(logger ?? NullLogger.Instance);
        var root = new Context(runtime, null, null, new Dictionary<string, object>());
        var rootFiber = Fiber.CreateRoot(root);
        return root.WithFiber(rootFiber);
    }

    /// <summary>The plugin instance that owns registrations made through this context.</summary>
    public Fiber? Fiber { get; }

    /// <summary>The registration boundary events dispatched through this context carry, when scoped.</summary>
    public ScopeKey? Scope { get; }

    /// <summary>Where the framework reports contained failures.</summary>
    public ILogger Logger => _runtime.Logger;

    /// <summary>Every service key currently published in this tree.</summary>
    public IReadOnlyList<string> ServiceKeys => _runtime.Keys;

    internal CordisRuntime Runtime => _runtime;

    private Context WithFiber(Fiber fiber) => new(_runtime, fiber, Scope, _values);

    internal Context ForFiber(Fiber fiber) => new(_runtime, fiber, Scope, _values);

    /// <summary>
    /// Derive a context bound to one registration boundary, so contributions and
    /// event listeners made through it are filtered to that boundary.
    /// </summary>
    /// <param name="scope">The boundary to bind to.</param>
    /// <returns>A context sharing this one's services and fiber, carrying the scope.</returns>
    public Context WithScope(ScopeKey scope) => new(_runtime, Fiber, scope, _values);

    /// <summary>
    /// Derive a context carrying one extra ambient value, reachable by
    /// <see cref="Value{T}" />.
    /// </summary>
    /// <param name="key">Name the value is reached by.</param>
    /// <param name="value">The value to carry.</param>
    /// <returns>A context sharing this one's services, fiber, and scope.</returns>
    public Context Extend(string key, object value)
    {
        var values = new Dictionary<string, object>(_values, StringComparer.Ordinal) { [key] = value };
        return new Context(_runtime, Fiber, Scope, values);
    }

    /// <summary>
    /// Read an ambient value added by <see cref="Extend" />.
    /// </summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="key">Name the value was added under.</param>
    /// <returns>The value, or null when this context carries none under that key.</returns>
    public T? Value<T>(string key) where T : class => _values.GetValueOrDefault(key) as T;

    // ---- services -------------------------------------------------------

    /// <summary>
    /// Read a service that may not be present.
    /// </summary>
    /// <typeparam name="T">The service's type.</typeparam>
    /// <param name="key">The service's context key.</param>
    /// <returns>The service, or null when nothing is published under that key.</returns>
    public T? Get<T>(string key) where T : class => _runtime.Get(key) as T;

    /// <summary>
    /// Read a service that the caller's declared injections guarantee.
    /// </summary>
    /// <typeparam name="T">The service's type.</typeparam>
    /// <param name="key">The service's context key.</param>
    /// <returns>The service.</returns>
    /// <exception cref="InvalidOperationException">Nothing is published under that key, or it has another type.</exception>
    public T Require<T>(string key) where T : class
    {
        var service = _runtime.Get(key);
        if (service is null) throw new InvalidOperationException($"service \"{key}\" is not available");
        if (service is not T typed)
        {
            throw new InvalidOperationException(
                $"service \"{key}\" is {service.GetType().Name}, not {typeof(T).Name}");
        }

        return typed;
    }

    /// <summary>
    /// Whether a service is currently published.
    /// </summary>
    /// <param name="key">The service's context key.</param>
    /// <returns>True while the key is claimed.</returns>
    public bool Has(string key) => _runtime.Has(key);

    /// <summary>
    /// Publish a service as an effect of the current plugin, so it is revoked when
    /// that plugin unloads.
    /// </summary>
    /// <param name="key">The context key to claim.</param>
    /// <param name="instance">The service instance.</param>
    /// <returns>A disposer revoking the service.</returns>
    /// <exception cref="InvalidOperationException">The key is already claimed.</exception>
    public IDisposable Provide(string key, object instance) => Effect(_runtime.Provide(key, instance));

    // ---- effects --------------------------------------------------------

    /// <summary>
    /// Attach one reversible registration to the current plugin.
    /// </summary>
    /// <param name="disposable">The teardown to own.</param>
    /// <returns>A disposer that unwinds just this effect.</returns>
    /// <remarks>Every contribution goes through here, which is why unloading a plugin removes its contributions.</remarks>
    public IDisposable Effect(IDisposable disposable)
    {
        var owner = Fiber ?? throw new InvalidOperationException("context has no owning fiber");
        return owner.AddEffect(disposable);
    }

    /// <summary>
    /// Attach one reversible registration whose teardown is asynchronous.
    /// </summary>
    /// <param name="teardown">The asynchronous teardown to own.</param>
    /// <returns>A disposer that unwinds just this effect, awaiting the teardown.</returns>
    public IAsyncDisposable EffectAsync(Func<ValueTask> teardown)
    {
        var owner = Fiber ?? throw new InvalidOperationException("context has no owning fiber");
        return owner.AddAsyncEffect(teardown);
    }

    // ---- plugins --------------------------------------------------------

    /// <summary>
    /// Mount one plugin beside the others. The plugin activates once every service
    /// it injects exists, and unloads — unwinding its effects — whenever one of
    /// them goes away, reactivating if it returns.
    /// </summary>
    /// <param name="plugin">The plugin to mount.</param>
    /// <returns>The fiber owning the mounted plugin, disposable to unmount it.</returns>
    public Fiber Plugin(IPlugin plugin)
    {
        var parent = Fiber ?? throw new InvalidOperationException("context has no owning fiber");
        return parent.MountChild(plugin);
    }

    // ---- event registration ---------------------------------------------

    /// <summary>
    /// Listen to an observe-only event.
    /// </summary>
    /// <typeparam name="TPayload">The event's payload.</typeparam>
    /// <param name="key">The event to listen on.</param>
    /// <param name="listener">Called for each dispatch; its failures are contained and logged.</param>
    /// <param name="prepend">Run ahead of ordinary registrations.</param>
    /// <returns>A disposer removing the listener.</returns>
    public IDisposable On<TPayload>(EmitKey<TPayload> key, Action<TPayload> listener, bool prepend = false)
        => Effect(_runtime.Events.Add(key.Name, listener, Scope, prepend));

    /// <summary>
    /// Listen to an event whose listeners run concurrently and are awaited.
    /// </summary>
    /// <typeparam name="TPayload">The event's payload.</typeparam>
    /// <param name="key">The event to listen on.</param>
    /// <param name="listener">Called for each dispatch.</param>
    /// <param name="prepend">Run ahead of ordinary registrations.</param>
    /// <returns>A disposer removing the listener.</returns>
    public IDisposable OnParallel<TPayload>(
        ParallelKey<TPayload> key,
        Func<TPayload, Task> listener,
        bool prepend = false)
        => Effect(_runtime.Events.Add(key.Name, listener, Scope, prepend));

    /// <summary>
    /// Listen to an event whose listeners run in order and are awaited one at a time.
    /// </summary>
    /// <typeparam name="TPayload">The event's payload.</typeparam>
    /// <param name="key">The event to listen on.</param>
    /// <param name="listener">Called for each dispatch; a failure propagates to the producer.</param>
    /// <param name="prepend">Run ahead of ordinary registrations.</param>
    /// <returns>A disposer removing the listener.</returns>
    public IDisposable OnSerial<TPayload>(
        SerialKey<TPayload> key,
        Func<TPayload, Task> listener,
        bool prepend = false)
        => Effect(_runtime.Events.Add(key.Name, listener, Scope, prepend));

    /// <summary>
    /// Listen to an around-middleware event.
    /// </summary>
    /// <typeparam name="TPayload">The event's payload.</typeparam>
    /// <typeparam name="TResult">The value the chain composes.</typeparam>
    /// <param name="key">The event to listen on.</param>
    /// <param name="listener">Receives the payload and a continuation; must call it to delegate.</param>
    /// <param name="prepend">Run ahead of ordinary registrations.</param>
    /// <returns>A disposer removing the listener.</returns>
    public IDisposable OnWaterfall<TPayload, TResult>(
        WaterfallKey<TPayload, TResult> key,
        WaterfallListener<TPayload, TResult> listener,
        bool prepend = false)
        => Effect(_runtime.Events.Add(key.Name, listener, Scope, prepend));

    // ---- dispatch --------------------------------------------------------

    /// <summary>
    /// Notify observers without awaiting them. Each listener's failure is contained
    /// and logged, so an observer can never break the producer.
    /// </summary>
    /// <typeparam name="TPayload">The event's payload.</typeparam>
    /// <param name="key">The event being dispatched.</param>
    /// <param name="payload">The payload each listener receives.</param>
    /// <param name="scope">Deliver only to listeners bound to this boundary and to unscoped ones.</param>
    public void Emit<TPayload>(EmitKey<TPayload> key, TPayload payload, ScopeKey? scope = null)
    {
        foreach (var listener in _runtime.Events.Snapshot(key.Name, scope ?? Scope))
        {
            try
            {
                ((Action<TPayload>)listener)(payload);
            }
            catch (Exception error)
            {
                _runtime.Logger.Log(LogLevel.Warn, key.Name, "listener failed", error);
            }
        }
    }

    /// <summary>
    /// Run every listener concurrently and await them all.
    /// </summary>
    /// <typeparam name="TPayload">The event's payload.</typeparam>
    /// <param name="key">The event being dispatched.</param>
    /// <param name="payload">The payload each listener receives.</param>
    /// <param name="scope">Deliver only to listeners bound to this boundary and to unscoped ones.</param>
    /// <returns>A task completing once every listener has settled.</returns>
    /// <exception cref="Exception">The first failure, rethrown after all listeners settled.</exception>
    public async Task ParallelAsync<TPayload>(ParallelKey<TPayload> key, TPayload payload, ScopeKey? scope = null)
    {
        var listeners = _runtime.Events.Snapshot(key.Name, scope ?? Scope);
        if (listeners.Count == 0) return;

        var running = new List<Task>(listeners.Count);
        foreach (var listener in listeners)
        {
            try
            {
                running.Add(((Func<TPayload, Task>)listener)(payload));
            }
            catch (Exception error)
            {
                running.Add(Task.FromException(error));
            }
        }

        Exception? first = null;
        foreach (var task in running)
        {
            try
            {
                await task;
            }
            catch (Exception error)
            {
                first ??= error;
            }
        }

        if (first is not null) throw first;
    }

    /// <summary>
    /// Run every listener in registration order, awaiting each before the next.
    /// </summary>
    /// <typeparam name="TPayload">The event's payload.</typeparam>
    /// <param name="key">The event being dispatched.</param>
    /// <param name="payload">The payload each listener receives.</param>
    /// <param name="scope">Deliver only to listeners bound to this boundary and to unscoped ones.</param>
    /// <returns>A task completing once every listener has run.</returns>
    /// <exception cref="Exception">A listener's failure, propagated to the producer.</exception>
    public async Task SerialAsync<TPayload>(SerialKey<TPayload> key, TPayload payload, ScopeKey? scope = null)
    {
        foreach (var listener in _runtime.Events.Snapshot(key.Name, scope ?? Scope))
        {
            await ((Func<TPayload, Task>)listener)(payload);
        }
    }

    /// <summary>
    /// Compose the registered listeners as around-middleware over a producer default.
    /// The first registration runs outermost; each delegates by calling its
    /// continuation, or short-circuits by returning without calling it.
    /// </summary>
    /// <typeparam name="TPayload">The event's payload.</typeparam>
    /// <typeparam name="TResult">The value the chain composes.</typeparam>
    /// <param name="key">The event being dispatched.</param>
    /// <param name="payload">The payload each listener receives.</param>
    /// <param name="terminal">The producer's default, run when the whole chain delegates.</param>
    /// <param name="scope">Deliver only to listeners bound to this boundary and to unscoped ones.</param>
    /// <returns>The value composed through the chain.</returns>
    public Task<TResult> WaterfallAsync<TPayload, TResult>(
        WaterfallKey<TPayload, TResult> key,
        TPayload payload,
        Func<Task<TResult>> terminal,
        ScopeKey? scope = null)
    {
        var listeners = _runtime.Events.Snapshot(key.Name, scope ?? Scope);
        var next = terminal;
        for (var index = listeners.Count - 1; index >= 0; index--)
        {
            var listener = (WaterfallListener<TPayload, TResult>)listeners[index];
            var downstream = next;
            next = () => listener(payload, downstream);
        }

        return next();
    }

    /// <summary>
    /// How many listeners are registered on one event, ignoring scope.
    /// </summary>
    /// <param name="name">The event name to count.</param>
    /// <returns>The registration count, for diagnostics and tests.</returns>
    [SuppressMessage("Design", "CA1024:Use properties where appropriate", Justification = "Takes an argument.")]
    public int ListenerCount(string name) => _runtime.Events.CountOf(name);
}

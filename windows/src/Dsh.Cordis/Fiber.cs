namespace Dsh.Cordis;

/// <summary>Lifecycle state of one mounted plugin.</summary>
public enum FiberState
{
    /// <summary>Mounted but waiting for a service it injects.</summary>
    Pending,

    /// <summary>Applied: its contributions are installed.</summary>
    Active,

    /// <summary>Unwinding its effects, either to wait again or to shut down.</summary>
    Unloading,

    /// <summary>Permanently unmounted; it will not activate again.</summary>
    Disposed,

    /// <summary>Its apply threw. The partial effects were unwound and it waits for its injections to cycle.</summary>
    Failed,
}

/// <summary>
/// One mounted plugin: the boundary that owns its contributions and unwinds them.
/// A fiber activates once every service its plugin injects exists, and unloads
/// again if one goes away, so load order is expressed as service requirements
/// rather than as a boot sequence.
/// </summary>
public sealed class Fiber : IAsyncDisposable
{
    private readonly IPlugin? _plugin;
    private readonly List<Func<ValueTask>> _effects = [];
    private readonly object _gate = new();
    private readonly SemaphoreSlim _transition = new(1, 1);
    private IDisposable? _watch;
    private Task _settled = Task.CompletedTask;
    private FiberState _state;
    private bool _wasReady;

    private Fiber(Context parentContext, IPlugin? plugin)
    {
        _plugin = plugin;
        Context = parentContext.ForFiber(this);
        _state = plugin is null ? FiberState.Active : FiberState.Pending;
    }

    /// <summary>The context contributions and child plugins are registered through.</summary>
    public Context Context { get; }

    /// <summary>The plugin's declared name, or <c>root</c> for the tree's own fiber.</summary>
    public string Name => _plugin?.Name ?? "root";

    /// <summary>Current lifecycle state.</summary>
    public FiberState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    /// <summary>The failure from the last apply attempt, while <see cref="State" /> is <see cref="FiberState.Failed" />.</summary>
    public Exception? Error { get; private set; }

    /// <summary>Service keys the plugin waits for; empty for a plugin with no injections.</summary>
    public IReadOnlyList<string> Inject => _plugin?.Inject ?? [];

    internal static Fiber CreateRoot(Context rootContext) => new(rootContext, null);

    internal Fiber MountChild(IPlugin plugin)
    {
        var child = new Fiber(Context, plugin);
        AddAsyncEffect(() => child.DisposeAsync());
        child.Start();
        return child;
    }

    private void Start()
    {
        _watch = Context.Runtime.Watch(OnServiceChanged);
        Reevaluate();
    }

    private void OnServiceChanged(string key)
    {
        if (Inject.Count > 0 && !Inject.Contains(key, StringComparer.Ordinal)) return;
        Reevaluate();
    }

    private bool IsReady()
    {
        foreach (var key in Inject)
        {
            if (!Context.Runtime.Has(key)) return false;
        }

        return true;
    }

    private void Reevaluate()
    {
        lock (_gate)
        {
            if (_state is FiberState.Disposed) return;
            _settled = Chain(_settled, TransitionAsync);
        }
    }

    private static Task Chain(Task previous, Func<Task> step)
        => previous.ContinueWith(
            static (_, state) => ((Func<Task>)state!)(),
            step,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Unwrap();

    private async Task TransitionAsync()
    {
        await _transition.WaitAsync();
        try
        {
            FiberState state;
            lock (_gate) state = _state;
            if (state is FiberState.Disposed) return;

            var ready = IsReady();
            var wasReady = _wasReady;
            _wasReady = ready;

            switch (state)
            {
                case FiberState.Active when !ready:
                    await UnwindAsync(FiberState.Pending);
                    break;
                case FiberState.Pending when ready:
                case FiberState.Failed when ready && !wasReady:
                    await ApplyAsync();
                    break;
                default:
                    break;
            }
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task ApplyAsync()
    {
        lock (_gate) _state = FiberState.Active;
        Error = null;
        try
        {
            await _plugin!.ApplyAsync(Context);
        }
        catch (Exception error)
        {
            Error = error;
            Context.Logger.Log(LogLevel.Error, Name, "plugin apply failed", error);
            await UnwindAsync(FiberState.Failed);
        }
    }

    private async Task UnwindAsync(FiberState next)
    {
        List<Func<ValueTask>> pending;
        lock (_gate)
        {
            if (_state is FiberState.Disposed && next is not FiberState.Disposed) return;
            _state = FiberState.Unloading;
            pending = [.. _effects];
            _effects.Clear();
        }

        for (var index = pending.Count - 1; index >= 0; index--)
        {
            try
            {
                await pending[index]();
            }
            catch (Exception error)
            {
                Context.Logger.Log(LogLevel.Warn, Name, "effect teardown failed", error);
            }
        }

        lock (_gate) _state = next;
    }

    internal IDisposable AddEffect(IDisposable disposable)
    {
        Func<ValueTask> teardown = () =>
        {
            disposable.Dispose();
            return default;
        };

        lock (_gate)
        {
            ThrowIfDisposed();
            _effects.Add(teardown);
        }

        return new ActionDisposable(() =>
        {
            bool owned;
            lock (_gate) owned = _effects.Remove(teardown);
            if (owned) disposable.Dispose();
        });
    }

    internal IAsyncDisposable AddAsyncEffect(Func<ValueTask> teardown)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _effects.Add(teardown);
        }

        return new AsyncEffectHandle(this, teardown);
    }

    /// <remarks>
    /// A disposed fiber rejects registration instead of silently accepting a
    /// contribution nothing would ever unwind.
    /// </remarks>
    private void ThrowIfDisposed()
    {
        if (_state is FiberState.Disposed)
        {
            throw new ObjectDisposedException(Name, $"plugin \"{Name}\" is disposed and rejects registration");
        }
    }

    private bool RemoveEffect(Func<ValueTask> teardown)
    {
        lock (_gate) return _effects.Remove(teardown);
    }

    /// <summary>
    /// Wait until this fiber's pending transitions have settled, following
    /// replacement work scheduled while waiting.
    /// </summary>
    /// <returns>A task completing once no transition is outstanding.</returns>
    public async Task WhenSettledAsync()
    {
        while (true)
        {
            Task settled;
            lock (_gate) settled = _settled;
            try
            {
                await settled;
            }
            catch (Exception error)
            {
                Context.Logger.Log(LogLevel.Warn, Name, "transition failed", error);
            }

            lock (_gate)
            {
                if (ReferenceEquals(settled, _settled)) return;
            }
        }
    }

    /// <summary>
    /// Unmount the plugin permanently: unwind its effects, including its child
    /// plugins, and stop reacting to service changes.
    /// </summary>
    /// <returns>A task completing once teardown has finished.</returns>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_state is FiberState.Disposed) return;
        }

        _watch?.Dispose();
        _watch = null;
        await WhenSettledAsync();

        await _transition.WaitAsync();
        try
        {
            await UnwindAsync(FiberState.Disposed);
        }
        finally
        {
            _transition.Release();
        }
    }

    private sealed class AsyncEffectHandle : IAsyncDisposable
    {
        private readonly Fiber _owner;
        private Func<ValueTask>? _teardown;

        public AsyncEffectHandle(Fiber owner, Func<ValueTask> teardown)
        {
            _owner = owner;
            _teardown = teardown;
        }

        public async ValueTask DisposeAsync()
        {
            var teardown = Interlocked.Exchange(ref _teardown, null);
            if (teardown is null) return;
            if (_owner.RemoveEffect(teardown)) await teardown();
        }
    }
}

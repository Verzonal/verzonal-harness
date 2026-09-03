using Dsh.Cordis;
using Dsh.Llm;

namespace Dsh.Agent;

/// <summary>What a caller gets back when it creates an agent.</summary>
/// <param name="Agent">The live agent.</param>
/// <param name="Teardown">Stops it and unwinds its registrations, awaiting quiescence first.</param>
public sealed record AgentHandle(IAgent Agent, Func<ValueTask> Teardown) : IAsyncDisposable
{
    /// <inheritdoc />
    public ValueTask DisposeAsync() => Teardown();
}

/// <summary>Builds live agents. One implementation is registered per deployment.</summary>
public interface IAgentFactory
{
    /// <summary>
    /// Create an agent over a fresh or resumed session.
    /// </summary>
    /// <param name="ownerCtx">The caller's context, which owns the agent's lifetime.</param>
    /// <param name="session">The session to drive.</param>
    /// <param name="options">The route the agent's requests use.</param>
    /// <param name="source">Why the session is starting.</param>
    /// <param name="cancellationToken">Cancels creation.</param>
    /// <returns>The published agent and its teardown.</returns>
    Task<AgentHandle> CreateAsync(
        Context ownerCtx,
        Session.Session session,
        AgentOptions options,
        SessionStartSource source,
        CancellationToken cancellationToken);
}

/// <summary>
/// The live registry of agents, and the ambient record of which agent a piece of
/// work is being done for.
/// </summary>
public sealed class AgentRegistry : Service
{
    private sealed class Entry
    {
        public required IAgent Agent { get; init; }
        public IAgent? Owner { get; init; }
        public bool Announced { get; set; }
    }

    private static readonly AsyncLocal<IAgent?> Initiator = new();

    private readonly Dictionary<SessionId, Entry> _entries = [];
    private readonly object _gate = new();

    /// <param name="ctx">The mounting plugin's context.</param>
    public AgentRegistry(Context ctx) : base(ctx, AgentKeys.Service) { }

    /// <summary>The factory that builds agents, once one is registered.</summary>
    public IAgentFactory? Factory { get; private set; }

    /// <summary>
    /// Register the deployment's agent factory.
    /// </summary>
    /// <param name="ctx">The registering context.</param>
    /// <param name="factory">The factory.</param>
    /// <returns>A disposer that withdraws it.</returns>
    /// <exception cref="InvalidOperationException">A factory is already registered.</exception>
    public IDisposable SetFactory(Context ctx, IAgentFactory factory)
    {
        lock (_gate)
        {
            if (Factory is not null) throw new InvalidOperationException("an agent factory is already registered");
            Factory = factory;
        }

        return ctx.Effect(new ActionDisposable(() =>
        {
            lock (_gate)
            {
                if (ReferenceEquals(Factory, factory)) Factory = null;
            }
        }));
    }

    /// <summary>
    /// Create an agent through the registered factory.
    /// </summary>
    /// <param name="ownerCtx">The caller's context.</param>
    /// <param name="session">The session to drive.</param>
    /// <param name="options">The route the agent's requests use.</param>
    /// <param name="source">Why the session is starting.</param>
    /// <param name="cancellationToken">Cancels creation.</param>
    /// <returns>The published agent and its teardown.</returns>
    /// <exception cref="InvalidOperationException">No factory is registered.</exception>
    public Task<AgentHandle> CreateAsync(
        Context ownerCtx,
        Session.Session session,
        AgentOptions options,
        SessionStartSource source = SessionStartSource.Startup,
        CancellationToken cancellationToken = default)
    {
        var factory = Factory ?? throw new InvalidOperationException("no agent factory is registered");
        return factory.CreateAsync(ownerCtx, session, options, source, cancellationToken);
    }

    /// <summary>
    /// Register an agent so it can be found by id.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <param name="owner">The agent that spawned it, when it is a child.</param>
    /// <returns>A disposer that detaches it and, if it was announced, reports the disposal.</returns>
    /// <exception cref="InvalidOperationException">The id is already registered.</exception>
    public IDisposable Enter(IAgent agent, IAgent? owner = null)
    {
        Entry entry;
        lock (_gate)
        {
            if (_entries.ContainsKey(agent.Id))
            {
                throw new InvalidOperationException($"agent \"{agent.Id}\" is already registered");
            }

            entry = new Entry { Agent = agent, Owner = owner };
            _entries[agent.Id] = entry;
        }

        return new ActionDisposable(() =>
        {
            bool announced;
            lock (_gate)
            {
                if (!_entries.TryGetValue(agent.Id, out var current) || !ReferenceEquals(current, entry)) return;
                _entries.Remove(agent.Id);
                announced = entry.Announced;
            }

            // A registration rolled back before it was announced gets no disposal
            // notice: nothing ever saw it exist.
            if (announced) Ctx.Emit(AgentKeys.Disposed, new AgentNotice(agent));
        });
    }

    /// <summary>
    /// Announce a registered agent, exactly once.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <exception cref="Exception">A listener's veto, which the caller turns into a rollback.</exception>
    public void Announce(IAgent agent)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(agent.Id, out var entry) || entry.Announced) return;
            entry.Announced = true;
        }

        Ctx.Emit(AgentKeys.Created, new AgentNotice(agent));
    }

    /// <summary>
    /// Find a registered agent.
    /// </summary>
    /// <param name="id">Its id.</param>
    /// <returns>The agent, or null when it is not live.</returns>
    public IAgent? Get(SessionId id)
    {
        lock (_gate) return _entries.GetValueOrDefault(id)?.Agent;
    }

    /// <summary>Every live agent.</summary>
    public IReadOnlyList<IAgent> Agents
    {
        get
        {
            lock (_gate) return [.. _entries.Values.Select(static entry => entry.Agent)];
        }
    }

    /// <summary>Live agents nothing else spawned.</summary>
    public IReadOnlyList<IAgent> Roots
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Values.Where(static entry => entry.Owner is null).Select(static entry => entry.Agent)];
            }
        }
    }

    /// <summary>
    /// Whether one agent spawned another.
    /// </summary>
    /// <param name="id">The possible child's id.</param>
    /// <param name="owner">The possible parent.</param>
    /// <returns>True when the parent spawned that agent.</returns>
    public bool IsOwnedBy(SessionId id, IAgent owner)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out var entry) && ReferenceEquals(entry.Owner, owner);
        }
    }

    /// <summary>
    /// The agent whose work the current async flow belongs to.
    /// </summary>
    /// <remarks>
    /// Attribution only. It records who asked for the work, never that the agent is
    /// still alive and never that the work is permitted.
    /// </remarks>
    public static IAgent? CurrentInitiator => Initiator.Value;

    /// <summary>
    /// The initiating agent, when one is required.
    /// </summary>
    /// <returns>The agent.</returns>
    /// <exception cref="InvalidOperationException">No agent is initiating this flow.</exception>
    public static IAgent RequireInitiator()
        => Initiator.Value ?? throw new InvalidOperationException("no initiating agent is active");

    /// <summary>
    /// Run work attributed to one agent.
    /// </summary>
    /// <typeparam name="T">What the work produces.</typeparam>
    /// <param name="agent">The initiating agent.</param>
    /// <param name="operation">The work.</param>
    /// <returns>The work's result.</returns>
    public static async Task<T> WithInitiatorAsync<T>(IAgent agent, Func<Task<T>> operation)
    {
        var previous = Initiator.Value;
        Initiator.Value = agent;
        try
        {
            return await operation();
        }
        finally
        {
            Initiator.Value = previous;
        }
    }

    /// <summary>
    /// Run work attributed to one agent.
    /// </summary>
    /// <param name="agent">The initiating agent.</param>
    /// <param name="operation">The work.</param>
    /// <returns>A task completing with the work.</returns>
    public static Task WithInitiatorAsync(IAgent agent, Func<Task> operation)
        => WithInitiatorAsync(agent, async () =>
        {
            await operation();
            return 0;
        });

    /// <summary>Mount the agent registry.</summary>
    /// <returns>The plugin to hand to <see cref="Context.Plugin" />.</returns>
    public static IPlugin Plugin()
        => ServicePlugin.Create("agent", AgentKeys.Service, ctx => new AgentRegistry(ctx));
}

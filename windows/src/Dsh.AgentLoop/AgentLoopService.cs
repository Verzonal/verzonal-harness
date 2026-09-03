using Dsh.Agent;
using Dsh.Cordis;
using Dsh.Session;

namespace Dsh.AgentLoop;

/// <summary>What the driver's behavior can be tuned to per deployment.</summary>
/// <param name="MaxParallelToolCalls">
/// How many tool calls may overlap in one step's pool. Deployment-varying, so it is
/// configuration on the plugin row rather than a constant in the loop.
/// </param>
public sealed record AgentLoopConfig(int MaxParallelToolCalls = 10)
{
    /// <summary>
    /// Reject a value the scheduler could not honor.
    /// </summary>
    /// <exception cref="InvalidOperationException">The pool size is not a positive integer.</exception>
    public void Validate()
    {
        if (MaxParallelToolCalls < 1)
        {
            throw new InvalidOperationException("agent-loop maxParallelToolCalls must be a positive integer");
        }
    }
}

/// <summary>
/// Builds and publishes agents driven by <see cref="ReactLoopAgent" />.
/// </summary>
/// <remarks>
/// Publication is a transaction: the session and agent are registered, then
/// announced, and any failure along the way unwinds in reverse. A creation listener
/// that refuses the agent therefore leaves nothing half-registered behind.
/// </remarks>
public sealed class AgentLoopService : Service, IAgentFactory
{
    /// <param name="ctx">The mounting plugin's context.</param>
    /// <param name="config">Deployment tuning for the driver.</param>
    public AgentLoopService(Context ctx, AgentLoopConfig config) : base(ctx, AgentKeys.LoopService)
    {
        config.Validate();
        Config = config;
    }

    /// <summary>The driver's deployment tuning.</summary>
    public AgentLoopConfig Config { get; }

    /// <inheritdoc />
    public Task<AgentHandle> CreateAsync(
        Context ownerCtx,
        Session.Session session,
        AgentOptions options,
        SessionStartSource source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var agents = Ctx.Require<AgentRegistry>(AgentKeys.Service);
        var sessions = Ctx.Get<SessionStore>(SessionKeys.Service);
        var agent = new ReactLoopAgent(Ctx, session, options, Config.MaxParallelToolCalls);

        IDisposable? detachSession = null;
        IDisposable? detachAgent = null;

        try
        {
            if (sessions is not null && sessions.Get(session.Id) is null)
            {
                detachSession = sessions.Enter(session);
            }

            detachAgent = agents.Enter(agent, ownerCtx.Value<ReactLoopAgent>("agent"));

            if (detachSession is not null) sessions!.Announce(session);
            agents.Announce(agent);
            Ctx.Emit(AgentKeys.SessionStart, new AgentSessionStart(agent, source), agent.Scope);
        }
        catch
        {
            detachAgent?.Dispose();
            detachSession?.Dispose();
            throw;
        }

        var disposed = false;
        var gate = new object();

        return Task.FromResult(new AgentHandle(agent, DisposeAsync));

        async ValueTask DisposeAsync()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
            }

            // Reverse teardown: stop the driver, wait for it to converge, then unwind
            // the registrations in the order opposite to how they were made.
            agent.Cancel(DisposedCancel.Instance);
            await agent.WhenIdleAsync();
            detachAgent?.Dispose();
            detachSession?.Dispose();
        }
    }

    /// <summary>Mount the driver and register it as the deployment's agent factory.</summary>
    /// <param name="config">Deployment tuning; the defaults when omitted.</param>
    /// <returns>The plugin to hand to <see cref="Context.Plugin" />.</returns>
    public static IPlugin Plugin(AgentLoopConfig? config = null)
        => new FunctionPlugin(
            "agent-loop",
            ctx =>
            {
                var service = new AgentLoopService(ctx, config ?? new AgentLoopConfig());
                ctx.Provide(AgentKeys.LoopService, service);
                ctx.Require<AgentRegistry>(AgentKeys.Service).SetFactory(ctx, service);
                return Task.CompletedTask;
            },
            AgentKeys.Service,
            LlmKeys(),
            SystemPromptKeys(),
            ToolKeys());

    private static string LlmKeys() => Llm.LlmKeys.Service;

    private static string SystemPromptKeys() => SystemPrompt.SystemPromptKeys.Service;

    private static string ToolKeys() => Tools.ToolKeys.Service;
}

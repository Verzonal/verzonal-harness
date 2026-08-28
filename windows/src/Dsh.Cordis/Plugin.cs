namespace Dsh.Cordis;

/// <summary>
/// A unit of composition. A plugin names the services it needs, and the framework
/// applies it once they exist and unwinds it if one goes away — so ordering is
/// expressed as requirements, never as a boot sequence.
/// </summary>
public interface IPlugin
{
    /// <summary>Stable name used in diagnostics and in the composition listing.</summary>
    string Name { get; }

    /// <summary>Context keys that must be published before this plugin applies.</summary>
    IReadOnlyList<string> Inject { get; }

    /// <summary>
    /// Install this plugin's contributions.
    /// </summary>
    /// <param name="ctx">The context owning every registration made here; it unwinds them on unload.</param>
    /// <returns>A task completing once the contributions are installed.</returns>
    Task ApplyAsync(Context ctx);
}

/// <summary>
/// A plugin defined by a delegate, for contributions that need no class of their own.
/// </summary>
public sealed class FunctionPlugin : IPlugin
{
    private readonly Func<Context, Task> _apply;

    /// <param name="name">Stable plugin name.</param>
    /// <param name="apply">Installs the contributions.</param>
    /// <param name="inject">Context keys required before applying.</param>
    public FunctionPlugin(string name, Func<Context, Task> apply, params string[] inject)
    {
        Name = name;
        _apply = apply;
        Inject = inject;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> Inject { get; }

    /// <inheritdoc />
    public Task ApplyAsync(Context ctx) => _apply(ctx);
}

/// <summary>
/// A capability published under one context key. Other plugins reach it by that key
/// rather than by importing this type, which is what lets a provider be swapped
/// from configuration.
/// </summary>
public abstract class Service
{
    /// <param name="ctx">The context this service was constructed against.</param>
    /// <param name="key">The context key it is published under.</param>
    protected Service(Context ctx, string key)
    {
        Ctx = ctx;
        Key = key;
    }

    /// <summary>The context this service registers its own contributions through.</summary>
    public Context Ctx { get; }

    /// <summary>The context key this service is published under.</summary>
    public string Key { get; }

    /// <summary>
    /// Prepare the service before it becomes reachable. Runs before the key is
    /// claimed, so a failure here publishes nothing.
    /// </summary>
    /// <returns>A task completing once the service is ready to serve.</returns>
    public virtual Task StartAsync() => Task.CompletedTask;

    /// <summary>
    /// Release the service's own resources. Runs after the key has been revoked, so
    /// no caller can reach a half-stopped service.
    /// </summary>
    /// <returns>A task completing once teardown has finished.</returns>
    public virtual ValueTask StopAsync() => default;
}

/// <summary>Mounts a <see cref="Service" /> as a plugin.</summary>
public static class ServicePlugin
{
    /// <summary>
    /// Build a plugin that constructs a service, starts it, publishes it under its
    /// key, and reverses all three on unload.
    /// </summary>
    /// <typeparam name="TService">The service type to mount.</typeparam>
    /// <param name="name">Stable plugin name.</param>
    /// <param name="key">The context key to publish under.</param>
    /// <param name="factory">Constructs the service against the plugin's own context.</param>
    /// <param name="inject">Context keys required before the service is constructed.</param>
    /// <returns>The plugin to hand to <see cref="Context.Plugin" />.</returns>
    public static IPlugin Create<TService>(
        string name,
        string key,
        Func<Context, TService> factory,
        params string[] inject)
        where TService : Service
        => new FunctionPlugin(
            name,
            async ctx =>
            {
                var service = factory(ctx);
                await service.StartAsync();

                // Registered before the key is claimed so the reverse unwind revokes
                // the service first and only then stops it: no caller can reach a
                // service that has already begun shutting down. The handle is
                // discarded because the fiber, not this scope, owns the teardown.
                _ = ctx.EffectAsync(service.StopAsync);
                ctx.Provide(key, service);
            },
            inject);
}

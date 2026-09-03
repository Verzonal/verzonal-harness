using Dsh.Cordis;

namespace Dsh.Llm;

/// <summary>Context and event keys the model capability publishes.</summary>
public static class LlmKeys
{
    /// <summary>The context key <see cref="LlmRuntime" /> is published under.</summary>
    public const string Service = "llm";

    /// <summary>
    /// Wraps one model request. Listeners implement retry, replay, and recording by
    /// composing around the adapter dispatch rather than by patching the loop.
    /// </summary>
    public static WaterfallKey<GenerateOptions, IAsyncEnumerable<StreamChunk>> Stream { get; } =
        new("llm/stream");

    /// <summary>The set of registered routes changed, so a model picker should refresh.</summary>
    public static EmitKey<IReadOnlyList<string>> AdaptersUpdated { get; } = new("llm/adapters-updated");
}

/// <summary>
/// The model capability: a registry of provider routes and the one path every model
/// request takes. Swapping which adapter serves a route changes the whole product's
/// model behavior without any consumer knowing.
/// </summary>
public sealed class LlmRuntime : Service
{
    private sealed record Registration(LlmAdapter Adapter, HashSet<string> Providers);

    private readonly Dictionary<string, Registration> _routes = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <param name="ctx">The mounting plugin's context.</param>
    public LlmRuntime(Context ctx) : base(ctx, LlmKeys.Service) { }

    /// <summary>
    /// Serve one or more routes with an adapter.
    /// </summary>
    /// <param name="providers">Route keys this adapter answers for.</param>
    /// <param name="adapter">The adapter instance.</param>
    /// <returns>A disposer that withdraws the routes.</returns>
    /// <exception cref="LlmError">One of the routes is already served.</exception>
    public IDisposable RegisterAdapter(IReadOnlyList<string> providers, LlmAdapter adapter)
    {
        var registration = new Registration(adapter, [.. providers]);
        lock (_gate)
        {
            foreach (var provider in registration.Providers)
            {
                if (_routes.ContainsKey(provider))
                {
                    throw new LlmError($"provider route \"{provider}\" already has an adapter", "DUPLICATE_ADAPTER");
                }
            }

            foreach (var provider in registration.Providers) _routes[provider] = registration;
        }

        Announce();

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                foreach (var provider in registration.Providers)
                {
                    if (_routes.TryGetValue(provider, out var current) && ReferenceEquals(current, registration))
                    {
                        _routes.Remove(provider);
                    }
                }
            }

            Announce();
        });
    }

    /// <summary>Every route currently served, in registration-key order.</summary>
    public IReadOnlyList<string> Providers
    {
        get
        {
            lock (_gate) return [.. _routes.Keys.OrderBy(static key => key, StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// Describe every served route.
    /// </summary>
    /// <returns>One entry per route, as a picker would show it.</returns>
    public IReadOnlyList<LlmProviderInfo> ListProviders()
    {
        var result = new List<LlmProviderInfo>();
        foreach (var provider in Providers)
        {
            var adapter = AdapterFor(provider);
            if (adapter is not null) result.Add(adapter.ProviderInfo(provider));
        }

        return result;
    }

    /// <summary>
    /// List one route's models.
    /// </summary>
    /// <param name="provider">The route key.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The route's catalog, empty when it advertises none.</returns>
    public async Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(
        string provider,
        CancellationToken cancellationToken = default)
    {
        var adapter = AdapterFor(provider);
        return adapter is null ? [] : await adapter.ListModelsAsync(provider, cancellationToken);
    }

    /// <summary>
    /// Resolve one exact model's capabilities.
    /// </summary>
    /// <param name="provider">The route key.</param>
    /// <param name="model">The provider-owned model id.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <returns>What the serving adapter knows about that model.</returns>
    /// <exception cref="LlmError">No adapter serves the route.</exception>
    public Task<LlmResolvedModelInfo> ResolveModelInfoAsync(
        string provider,
        string model,
        CancellationToken cancellationToken = default)
        => RequireAdapter(provider).ResolveModelAsync(provider, model, cancellationToken);

    /// <summary>
    /// Bind a configuration to the adapter that will serve it, materializing the
    /// defaults that adapter applies.
    /// </summary>
    /// <param name="config">The proposed configuration.</param>
    /// <param name="cancellationToken">Cancels the preparation.</param>
    /// <returns>The prepared call, carrying the exact configuration the request will use.</returns>
    /// <exception cref="LlmError">No adapter serves the route.</exception>
    public async Task<IPreparedLlmCall> PrepareCallAsync(
        LlmCallConfig config,
        CancellationToken cancellationToken = default)
    {
        var adapter = RequireAdapter(config.Provider);
        var model = await adapter.ResolveModelAsync(config.Provider, config.Model, cancellationToken);

        var effort = config.ReasoningEffort;
        var effortFromAdapter = false;
        if (effort is null && model.Reasoning?.DefaultEffort is { } defaultEffort)
        {
            effort = defaultEffort;
            effortFromAdapter = true;
        }

        var maxTokens = config.MaxTokens;
        var maxTokensFromAdapter = false;
        if (maxTokens is null && model.DefaultMaxTokens is { } defaultMaxTokens)
        {
            maxTokens = defaultMaxTokens;
            maxTokensFromAdapter = true;
        }

        var resolved = config with { ReasoningEffort = effort, MaxTokens = maxTokens };
        var defaults = new LlmCallConfigAdapterDefaults(effortFromAdapter, maxTokensFromAdapter);
        var retry = adapter.ProviderRetryPolicy(config.Provider) ?? new ResolvedRetryPolicy();
        return new PreparedLlmCall(adapter, resolved, defaults, model, retry);
    }

    /// <summary>
    /// Dispatch one model request through the <see cref="LlmKeys.Stream" /> waterfall.
    /// </summary>
    /// <param name="options">The request to send.</param>
    /// <param name="cancellationToken">Cancels the stream.</param>
    /// <returns>
    /// The chunk stream. Adapter failures arrive as a terminal finish rather than as
    /// an exception, so one consumer path handles every outcome.
    /// </returns>
    public IAsyncEnumerable<StreamChunk> StreamAsync(
        GenerateOptions options,
        CancellationToken cancellationToken = default)
    {
        var wrapped = Ctx.WaterfallAsync(
            LlmKeys.Stream,
            options,
            () => Task.FromResult(DispatchAsync(options, cancellationToken)));

        return Flatten(wrapped, cancellationToken);
    }

    private static async IAsyncEnumerable<StreamChunk> Flatten(
        Task<IAsyncEnumerable<StreamChunk>> pending,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IAsyncEnumerable<StreamChunk>? stream = null;
        FinishChunk? failed = null;
        try
        {
            stream = await pending;
        }
        catch (Exception error)
        {
            failed = AdapterStream.FailureChunk(error, cancellationToken);
        }

        if (failed is not null || stream is null)
        {
            yield return failed ?? AdapterStream.FailureChunk(
                new LlmError("stream middleware produced no stream", LlmErrorCodes.EmptyResponse),
                cancellationToken);
            yield break;
        }

        await foreach (var chunk in stream.WithCancellation(cancellationToken))
        {
            yield return chunk;
        }
    }

    private IAsyncEnumerable<StreamChunk> DispatchAsync(GenerateOptions options, CancellationToken cancellationToken)
    {
        var adapter = AdapterFor(options.Provider);
        if (adapter is null)
        {
            return Single(new FinishChunk(new ErrorFinish(new LlmFailure(
                $"no adapter serves provider route \"{options.Provider}\"",
                LlmErrorCodes.NoAdapter))));
        }

        return AdapterStream.Contained(adapter, ProjectForAdapter(options, adapter), cancellationToken);
    }

    private static async IAsyncEnumerable<StreamChunk> Single(StreamChunk chunk)
    {
        await Task.CompletedTask;
        yield return chunk;
    }

    /// <summary>
    /// Prepare history for one adapter: strip replay metadata the target adapter did
    /// not itself produce, since it is adapter-private and meaningless elsewhere.
    /// </summary>
    private GenerateOptions ProjectForAdapter(GenerateOptions options, LlmAdapter adapter)
    {
        List<Message>? rewritten = null;
        for (var index = 0; index < options.Messages.Count; index++)
        {
            var message = options.Messages[index];
            if (message.Source is not ModelMessageSource { ReplayState: not null } source) continue;
            if (ReferenceEquals(AdapterFor(source.Provider), adapter)) continue;

            rewritten ??= [.. options.Messages];
            rewritten[index] = message with { Source = source with { ReplayState = null } };
        }

        return rewritten is null ? options : options with { Messages = rewritten };
    }

    private LlmAdapter? AdapterFor(string provider)
    {
        lock (_gate) return _routes.GetValueOrDefault(provider)?.Adapter;
    }

    private LlmAdapter RequireAdapter(string provider)
        => AdapterFor(provider)
           ?? throw new LlmError(
               $"no adapter serves provider route \"{provider}\"",
               LlmErrorCodes.NoAdapter);

    private void Announce() => Ctx.Emit(LlmKeys.AdaptersUpdated, Providers);

    /// <summary>Mount the model capability.</summary>
    /// <returns>The plugin to hand to <see cref="Context.Plugin" />.</returns>
    public static IPlugin Plugin() => ServicePlugin.Create("llm", LlmKeys.Service, ctx => new LlmRuntime(ctx));
}

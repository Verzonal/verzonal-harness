using System.Runtime.CompilerServices;

namespace Dsh.Llm;

/// <summary>What kind of input a model accepts.</summary>
public enum ModelModality
{
    /// <summary>Plain text.</summary>
    Text,

    /// <summary>Images alongside text.</summary>
    Image,
}

/// <summary>One registered provider route, as a picker would show it.</summary>
/// <param name="Id">The route key requests name.</param>
/// <param name="Name">Display name.</param>
public sealed record LlmProviderInfo(string Id, string Name);

/// <summary>One model a provider offers.</summary>
/// <param name="Provider">The route it belongs to.</param>
/// <param name="Id">The provider-owned model id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">One line about what it is for.</param>
/// <param name="InputModalities">What it accepts; text-only when omitted.</param>
public sealed record LlmModelInfo(
    string Provider,
    string Id,
    string Name,
    string? Description = null,
    IReadOnlyList<ModelModality>? InputModalities = null);

/// <summary>How much a model can hold.</summary>
/// <param name="ContextWindow">Combined request and response ceiling, in tokens.</param>
public sealed record LlmModelContext(int ContextWindow);

/// <summary>One thinking level a model advertises.</summary>
/// <param name="Id">The effort id sent on the wire.</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">One line about what it costs and buys.</param>
public sealed record LlmReasoningEffortInfo(ReasoningEffortId Id, string Name, string? Description = null);

/// <summary>The thinking levels a model offers.</summary>
/// <param name="Efforts">Every level, in the order a picker should show them.</param>
/// <param name="DefaultEffort">The level used when the caller names none.</param>
public sealed record LlmModelReasoningInfo(
    IReadOnlyList<LlmReasoningEffortInfo> Efforts,
    ReasoningEffortId? DefaultEffort = null);

/// <summary>
/// Everything the adapter knows about one exact model, resolved when a call is
/// prepared rather than guessed from a catalog.
/// </summary>
/// <param name="Info">The model's identity.</param>
/// <param name="Context">Its capacity, when advertised.</param>
/// <param name="DefaultMaxTokens">The output ceiling it applies when the caller names none.</param>
/// <param name="Reasoning">Its thinking levels, when it offers a choice.</param>
public sealed record LlmResolvedModelInfo(
    LlmModelInfo Info,
    LlmModelContext? Context = null,
    int? DefaultMaxTokens = null,
    LlmModelReasoningInfo? Reasoning = null);

/// <summary>How a provider wants its failures retried.</summary>
/// <param name="MaxRetries">How many attempts follow the first.</param>
/// <param name="InitialDelayMs">Backoff before the first retry.</param>
/// <param name="MaxDelayMs">Ceiling the exponential backoff saturates at.</param>
/// <param name="JitterRatio">Fraction of the delay randomized, to spread retries apart.</param>
/// <param name="RetryableCodes">Failure codes worth retrying; everything else is terminal.</param>
public sealed record ResolvedRetryPolicy(
    int MaxRetries = 5,
    double InitialDelayMs = 500,
    double MaxDelayMs = 10_000,
    double JitterRatio = 0.1,
    IReadOnlyList<string>? RetryableCodes = null)
{
    /// <summary>The codes retried when a provider names none.</summary>
    public static IReadOnlyList<string> DefaultRetryableCodes { get; } =
    [
        LlmErrorCodes.EmptyResponse,
        LlmErrorCodes.RateLimit,
        LlmErrorCodes.Server,
        LlmErrorCodes.Timeout,
        LlmErrorCodes.Transport,
    ];

    /// <summary>
    /// Whether a failure is worth another attempt.
    /// </summary>
    /// <param name="code">The failure's code.</param>
    /// <returns>True when the code is in this policy's retryable set.</returns>
    public bool IsRetryable(string code)
        => (RetryableCodes ?? DefaultRetryableCodes).Contains(code, StringComparer.Ordinal);

    /// <summary>
    /// The backoff before one retry: exponential, capped, then jittered.
    /// </summary>
    /// <param name="attempt">Which retry this is, counting from 1.</param>
    /// <param name="random">Source of the jitter fraction, in [0, 1).</param>
    /// <returns>The delay in milliseconds.</returns>
    public double DelayFor(int attempt, double random)
    {
        var exponent = Math.Min(attempt - 1, 30);
        var exponential = Math.Min(InitialDelayMs * Math.Pow(2, exponent), MaxDelayMs);
        var jitter = 1 - JitterRatio + (2 * JitterRatio * random);
        return Math.Min(exponential * jitter, MaxDelayMs);
    }
}

/// <summary>
/// One model call bound to the exact adapter registration that resolved its
/// defaults, so the request that runs is the request that was measured.
/// </summary>
public interface IPreparedLlmCall
{
    /// <summary>The configuration with the adapter's defaults materialized.</summary>
    LlmCallConfig Config { get; }

    /// <summary>Which configuration fields the adapter supplied.</summary>
    LlmCallConfigAdapterDefaults AdapterDefaults { get; }

    /// <summary>The model's capacity, when advertised.</summary>
    LlmModelContext? Context { get; }

    /// <summary>What the model accepts as input.</summary>
    IReadOnlyList<ModelModality>? InputModalities { get; }

    /// <summary>How this provider wants failures retried.</summary>
    ResolvedRetryPolicy RetryPolicy { get; }

    /// <summary>
    /// Dispatch the request through the bound adapter.
    /// </summary>
    /// <param name="options">The request to send.</param>
    /// <param name="cancellationToken">Cancels the stream.</param>
    /// <returns>The model's chunk stream.</returns>
    IAsyncEnumerable<StreamChunk> StreamAsync(GenerateOptions options, CancellationToken cancellationToken);
}

/// <summary>
/// What a model provider implements. Only <see cref="StreamAsync" /> is required;
/// every other member has a default that describes a plain text-only route.
/// </summary>
public abstract class LlmAdapter
{
    /// <summary>
    /// Describe one route this adapter serves.
    /// </summary>
    /// <param name="provider">The route key.</param>
    /// <returns>The route's display identity.</returns>
    public virtual LlmProviderInfo ProviderInfo(string provider) => new(provider, provider);

    /// <summary>
    /// The retry policy this adapter wants applied to one route's failures.
    /// </summary>
    /// <param name="provider">The route key.</param>
    /// <returns>The policy, or null to accept the harness default.</returns>
    public virtual ResolvedRetryPolicy? ProviderRetryPolicy(string provider) => null;

    /// <summary>
    /// List the models one route offers.
    /// </summary>
    /// <param name="provider">The route key.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The catalog, which may be empty for a route that accepts any id.</returns>
    public virtual Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(
        string provider,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<LlmModelInfo>>([]);

    /// <summary>
    /// Resolve one exact model's capabilities.
    /// </summary>
    /// <param name="provider">The route key.</param>
    /// <param name="model">The provider-owned model id.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <returns>What the adapter knows about that model.</returns>
    public virtual Task<LlmResolvedModelInfo> ResolveModelAsync(
        string provider,
        string model,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new LlmResolvedModelInfo(new LlmModelInfo(provider, model, model)));

    /// <summary>
    /// Stream one model request.
    /// </summary>
    /// <param name="options">The request to send.</param>
    /// <param name="cancellationToken">Cancels the stream.</param>
    /// <returns>
    /// The chunk stream, ending in exactly one <see cref="FinishChunk" />. An adapter
    /// may throw instead; the runtime normalizes that into a terminal finish.
    /// </returns>
    public abstract IAsyncEnumerable<StreamChunk> StreamAsync(
        GenerateOptions options,
        CancellationToken cancellationToken);
}

/// <summary>A prepared call bound to one adapter instance.</summary>
internal sealed class PreparedLlmCall : IPreparedLlmCall
{
    private readonly LlmAdapter _adapter;

    public PreparedLlmCall(
        LlmAdapter adapter,
        LlmCallConfig config,
        LlmCallConfigAdapterDefaults adapterDefaults,
        LlmResolvedModelInfo model,
        ResolvedRetryPolicy retryPolicy)
    {
        _adapter = adapter;
        Config = config;
        AdapterDefaults = adapterDefaults;
        Context = model.Context;
        InputModalities = model.Info.InputModalities;
        RetryPolicy = retryPolicy;
    }

    public LlmCallConfig Config { get; }

    public LlmCallConfigAdapterDefaults AdapterDefaults { get; }

    public LlmModelContext? Context { get; }

    public IReadOnlyList<ModelModality>? InputModalities { get; }

    public ResolvedRetryPolicy RetryPolicy { get; }

    public IAsyncEnumerable<StreamChunk> StreamAsync(GenerateOptions options, CancellationToken cancellationToken)
        => AdapterStream.Contained(_adapter, options, cancellationToken);
}

/// <summary>
/// Wraps an adapter stream so every failure becomes a terminal chunk instead of an
/// exception, which is what lets one consumer path handle success and failure alike.
/// </summary>
internal static class AdapterStream
{
    public static async IAsyncEnumerable<StreamChunk> Contained(
        LlmAdapter adapter,
        GenerateOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IAsyncEnumerator<StreamChunk>? enumerator = null;
        FinishChunk? failed = null;
        try
        {
            enumerator = adapter.StreamAsync(options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception error)
        {
            failed = FailureChunk(error, cancellationToken);
        }

        if (failed is not null || enumerator is null)
        {
            yield return failed ?? FailureChunk(
                new LlmError("adapter produced no stream", LlmErrorCodes.EmptyResponse),
                cancellationToken);
            yield break;
        }

        try
        {
            while (true)
            {
                StreamChunk? chunk = null;
                var completed = false;
                try
                {
                    if (await enumerator.MoveNextAsync()) chunk = enumerator.Current;
                    else completed = true;
                }
                catch (Exception error)
                {
                    failed = FailureChunk(error, cancellationToken);
                }

                if (failed is not null)
                {
                    yield return failed;
                    yield break;
                }

                if (completed) yield break;
                yield return chunk!;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    /// <summary>
    /// Classify a dispatch failure as the stream's terminal chunk.
    /// </summary>
    /// <param name="error">What the adapter threw.</param>
    /// <param name="cancellationToken">The caller's token, which decides abort versus error.</param>
    /// <returns>A finish chunk carrying the normalized failure.</returns>
    public static FinishChunk FailureChunk(Exception error, CancellationToken cancellationToken)
    {
        var failure = LlmFailures.Normalize(error);
        var aborted = cancellationToken.IsCancellationRequested
            || string.Equals(failure.Code, LlmErrorCodes.Aborted, StringComparison.Ordinal);
        return new FinishChunk(aborted ? new AbortedFinish(failure) : new ErrorFinish(failure));
    }
}

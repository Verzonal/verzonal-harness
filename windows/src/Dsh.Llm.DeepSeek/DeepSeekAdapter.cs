using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Dsh.Cordis;
using Dsh.Credentials;

namespace Dsh.Llm.DeepSeek;

/// <summary>How the DeepSeek provider is composed.</summary>
/// <param name="Provider">The route key this adapter answers for.</param>
/// <param name="BaseUrl">The API root; the public endpoint when unset.</param>
/// <param name="ApiKeyEnv">The credential name the key is resolved under.</param>
/// <param name="StreamIdleTimeoutMs">
/// How long a stream may go quiet before it is abandoned. This is an idle timeout,
/// not a total one: a model that is thinking hard is still working, and killing it
/// for taking a while would be wrong.
/// </param>
/// <param name="ContextWindow">The models' capacity, in tokens.</param>
/// <param name="DefaultMaxTokens">The output ceiling applied when a caller names none.</param>
/// <param name="Thinking">Whether the deployment permits thinking mode at all.</param>
public sealed record DeepSeekConfig(
    string Provider = "deepseek-official",
    string BaseUrl = "https://api.deepseek.com",
    string ApiKeyEnv = "DEEPSEEK_API_KEY",
    int StreamIdleTimeoutMs = 300_000,
    int ContextWindow = 1_000_000,
    int DefaultMaxTokens = 256_000,
    bool Thinking = true)
{
    /// <summary>The environment variable that overrides the API root.</summary>
    public const string BaseUrlEnvironmentVariable = "DEEPSEEK_BASE_URL";

    /// <summary>The model used when a caller names none.</summary>
    public const string DefaultModel = "deepseek-v4-flash";

    /// <summary>
    /// Resolve the API root, letting the environment override the composed value.
    /// </summary>
    /// <returns>The API root, without a trailing slash.</returns>
    public string ResolveBaseUrl()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(BaseUrlEnvironmentVariable);
        var chosen = string.IsNullOrWhiteSpace(fromEnvironment) ? BaseUrl : fromEnvironment.Trim();
        return chosen.TrimEnd('/');
    }
}

/// <summary>
/// Calls DeepSeek's chat-completions API.
/// </summary>
/// <remarks>
/// The credential is looked up per request rather than captured, so a key the user
/// changes takes effect on the next turn and no long-lived object keeps a stale
/// secret. Catalog membership is advisory: an id the catalog does not list still
/// works as a text route, because the provider adds models faster than a hardcoded
/// list can follow.
/// </remarks>
public sealed class DeepSeekAdapter : LlmAdapter, IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly IReadOnlyList<string> Efforts = ["off", "low", "high", "max"];

    private readonly DeepSeekConfig _config;
    private readonly Func<string?> _apiKey;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <param name="config">How the provider is composed.</param>
    /// <param name="apiKey">Resolves the API key at request time.</param>
    /// <param name="http">The client to send with; one is created when omitted.</param>
    public DeepSeekAdapter(DeepSeekConfig config, Func<string?> apiKey, HttpClient? http = null)
    {
        _config = config;
        _apiKey = apiKey;
        _ownsHttp = http is null;

        // No total request timeout: the idle watchdog below decides when a stream has
        // actually stopped, and a client-level deadline would cut off a working model.
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>The models this build advertises; others still work as text routes.</summary>
    public static IReadOnlyList<(string Id, string Name, bool Vision)> Catalog { get; } =
    [
        (DeepSeekConfig.DefaultModel, "DeepSeek-V4-Flash", false),
        ("deepseek-v4-pro", "DeepSeek-V4-Pro", false),
        ("deepseek-v4-flash-vision-exp", "DeepSeek-V4-Flash-Vision", true),
    ];

    /// <inheritdoc />
    public override LlmProviderInfo ProviderInfo(string provider) => new(provider, "DeepSeek");

    /// <inheritdoc />
    public override ResolvedRetryPolicy? ProviderRetryPolicy(string provider) => new();

    /// <inheritdoc />
    public override Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(
        string provider,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<LlmModelInfo>>(
        [
            .. Catalog.Select(entry => new LlmModelInfo(
                provider,
                entry.Id,
                entry.Name,
                null,
                entry.Vision ? [ModelModality.Text, ModelModality.Image] : [ModelModality.Text])),
        ]);

    /// <inheritdoc />
    public override Task<LlmResolvedModelInfo> ResolveModelAsync(
        string provider,
        string model,
        CancellationToken cancellationToken = default)
    {
        var known = Catalog.FirstOrDefault(entry => string.Equals(entry.Id, model, StringComparison.Ordinal));
        var info = new LlmModelInfo(
            provider,
            model,
            known.Name ?? model,
            null,
            known.Vision ? [ModelModality.Text, ModelModality.Image] : [ModelModality.Text]);

        return Task.FromResult(new LlmResolvedModelInfo(
            info,
            new LlmModelContext(_config.ContextWindow),
            _config.DefaultMaxTokens,
            new LlmModelReasoningInfo(
                [.. Efforts.Select(static effort => new LlmReasoningEffortInfo(new ReasoningEffortId(effort), effort))],
                new ReasoningEffortId(_config.Thinking ? "high" : "off"))));
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<StreamChunk> StreamAsync(
        GenerateOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var key = _apiKey()
                  ?? throw new LlmError(
                      $"no API key for provider route \"{_config.Provider}\"; set {_config.ApiKeyEnv} or save one in the credentials document",
                      LlmErrorCodes.MissingCredential);

        var baseUrl = _config.ResolveBaseUrl();
        var payload = JsonSerializer.Serialize(BuildRequest(options), Json);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.UserAgent.ParseAdd("deepseek-harness-windows/0.1.0");
        if (options.SessionId is { } sessionId) request.Headers.TryAddWithoutValidation("x-dsh-session-id", sessionId.Value);
        if (options.Purpose == RequestPurpose.Compaction) request.Headers.TryAddWithoutValidation("x-dsh-compact", "1");

        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(_config.StreamIdleTimeoutMs);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new LlmError($"the request to {baseUrl} failed", LlmErrorCodes.Transport, error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmError(
                $"the request to {baseUrl} went quiet for {_config.StreamIdleTimeoutMs}ms",
                LlmErrorCodes.Timeout);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await FailureAsync(response, baseUrl);
            }

            var body = await response.Content.ReadAsStreamAsync(idle.Token);
            var translator = new StreamTranslator();

            await foreach (var payloadText in ReadPayloadsAsync(body, idle, cancellationToken))
            {
                if (string.Equals(payloadText, SseReader.Done, StringComparison.Ordinal)) break;

                WireChunk? chunk;
                try
                {
                    chunk = JsonSerializer.Deserialize<WireChunk>(payloadText, Json);
                }
                catch (JsonException error)
                {
                    var excerpt = payloadText.Length > 120 ? payloadText[..120] + "…" : payloadText;
                    throw new LlmError(
                        $"the provider sent a chunk that could not be parsed: {excerpt}",
                        LlmErrorCodes.MalformedResponse,
                        error);
                }

                if (chunk is null) continue;
                foreach (var translated in translator.Push(chunk)) yield return translated;
            }

            foreach (var translated in translator.Complete()) yield return translated;
        }
    }

    /// <summary>
    /// Read the stream's payloads, keeping the idle watchdog armed.
    /// </summary>
    /// <remarks>
    /// The deadline is pushed forward on every payload and on every keep-alive
    /// comment, so it measures silence on the wire rather than how long the whole
    /// answer takes.
    /// </remarks>
    private async IAsyncEnumerable<string> ReadPayloadsAsync(
        Stream body,
        CancellationTokenSource idle,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payloads = SseReader.ReadAsync(
            body,
            _ => idle.CancelAfter(_config.StreamIdleTimeoutMs),
            idle.Token).GetAsyncEnumerator(idle.Token);

        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await payloads.MoveNextAsync();
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new LlmError(
                        $"the model stream went quiet for {_config.StreamIdleTimeoutMs}ms",
                        LlmErrorCodes.Timeout);
                }

                if (!moved) yield break;
                idle.CancelAfter(_config.StreamIdleTimeoutMs);
                yield return payloads.Current;
            }
        }
        finally
        {
            await payloads.DisposeAsync();
        }
    }

    /// <summary>
    /// Build the request body.
    /// </summary>
    /// <param name="options">What the loop asked for.</param>
    /// <returns>The provider's request.</returns>
    internal WireRequest BuildRequest(GenerateOptions options)
    {
        var (thinking, effort) = ResolveThinking(options);

        return new WireRequest(
            string.IsNullOrEmpty(options.Model) ? DeepSeekConfig.DefaultModel : options.Model,
            WireSerializer.Serialize(options.System, options.Messages),
            true,
            new WireStreamOptions(true),
            thinking is null ? null : new WireThinking(thinking),
            effort,
            WireSerializer.SerializeTools(options.Tools),
            options.Config.Temperature,
            options.Config.MaxTokens,
            options.Config.Stop);
    }

    /// <summary>
    /// Decide whether this request thinks, and how hard.
    /// </summary>
    /// <param name="options">What the loop asked for.</param>
    /// <returns>The thinking toggle and effort to send, either of which may be absent.</returns>
    /// <exception cref="LlmError">Effort was asked for on a deployment that disabled thinking.</exception>
    internal (string? Thinking, string? Effort) ResolveThinking(GenerateOptions options)
    {
        // Naming a session is not the conversation, so it never spends thinking tokens.
        if (options.Purpose == RequestPurpose.SessionTitle) return ("disabled", null);

        var effort = options.Config.ReasoningEffort?.Value;

        if (!_config.Thinking)
        {
            if (effort is not null and not "off")
            {
                throw new LlmError(
                    $"this deployment disabled thinking mode, so reasoning effort \"{effort}\" cannot be used",
                    LlmErrorCodes.UnsupportedReasoningEffort);
            }

            return ("disabled", null);
        }

        // "off" is the harness's way of saying no thinking; it never goes on the wire.
        if (effort is null or "off") return (effort is null ? null : "disabled", null);

        return ("enabled", effort);
    }

    private static async Task<LlmError> FailureAsync(HttpResponseMessage response, string baseUrl)
    {
        var status = (int)response.StatusCode;
        var body = await response.Content.ReadAsStringAsync();

        WireErrorDetail? detail = null;
        try
        {
            detail = JsonSerializer.Deserialize<WireErrorBody>(body, Json)?.Error;
        }
        catch (JsonException)
        {
            // The status is authoritative; a body that will not parse just means there
            // is no extra detail to report.
        }

        var described = string.Join(' ', new[] { detail?.Code, detail?.Type, detail?.Message }.Where(
            static part => !string.IsNullOrEmpty(part)));

        var failure = new LlmFailure(
            detail?.Message ?? $"{baseUrl} returned HTTP {status}",
            ClassifyStatus(status, described),
            status,
            RetryAfterMs(response),
            RequestIdOf(response));

        return new LlmError(failure);
    }

    /// <summary>
    /// Classify an HTTP failure.
    /// </summary>
    /// <param name="status">The response status.</param>
    /// <param name="detail">The provider's code, type, and message joined together.</param>
    /// <returns>The harness code that decides whether policy retries it.</returns>
    internal static string ClassifyStatus(int status, string? detail)
    {
        if (status is 401 or 403) return LlmErrorCodes.InvalidCredential;
        if (FailureClassifier.IsQuotaExceeded(detail)) return LlmErrorCodes.QuotaExceeded;
        if (status == 429) return LlmErrorCodes.RateLimit;
        if (status == 413) return LlmErrorCodes.InvalidRequest;
        if (status == 400)
        {
            return FailureClassifier.IsContextWindowExceeded(detail)
                ? LlmErrorCodes.ContextWindowExceeded
                : LlmErrorCodes.InvalidRequest;
        }

        return status >= 500 ? LlmErrorCodes.Server : $"HTTP_{status}";
    }

    private static double? RetryAfterMs(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero) return delta.TotalMilliseconds;
        if (retryAfter?.Date is { } date)
        {
            var wait = (date - DateTimeOffset.UtcNow).TotalMilliseconds;
            if (wait > 0) return wait;
        }

        return null;
    }

    private static ProviderRequestId? RequestIdOf(HttpResponseMessage response)
    {
        foreach (var header in new[] { "x-request-id", "x-deepseek-request-id" })
        {
            if (response.Headers.TryGetValues(header, out var values))
            {
                var first = values.FirstOrDefault();
                if (!string.IsNullOrEmpty(first)) return new ProviderRequestId(first);
            }
        }

        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    /// <summary>
    /// Mount the DeepSeek provider.
    /// </summary>
    /// <param name="config">How the provider is composed; the public endpoint when omitted.</param>
    /// <returns>The plugin to hand to <see cref="Context.Plugin" />.</returns>
    public static IPlugin Plugin(DeepSeekConfig? config = null)
    {
        var resolved = config ?? new DeepSeekConfig();
        return new FunctionPlugin(
            "llm-deepseek",
            ctx =>
            {
                var llm = ctx.Require<LlmRuntime>(LlmKeys.Service);
                var credentials = ctx.Get<CredentialProvider>(CredentialKeys.Service);

                var adapter = new DeepSeekAdapter(
                    resolved,
                    () => credentials?.Resolve(resolved.ApiKeyEnv)?.Value
                          ?? Environment.GetEnvironmentVariable(resolved.ApiKeyEnv));

                ctx.Effect(adapter);
                ctx.Effect(llm.RegisterAdapter([resolved.Provider], adapter));
                return Task.CompletedTask;
            },
            LlmKeys.Service);
    }
}

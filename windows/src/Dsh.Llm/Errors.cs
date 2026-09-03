namespace Dsh.Llm;

/// <summary>
/// A failure carrying a machine-readable code, so callers branch on the code rather
/// than on message text.
/// </summary>
public class HarnessError : Exception
{
    /// <param name="message">Human-readable description.</param>
    /// <param name="code">Machine-readable classification.</param>
    /// <param name="innerException">The failure this one wraps, when there is one.</param>
    public HarnessError(string message, string code, Exception? innerException = null)
        : base(message, innerException)
        => Code = code;

    /// <summary>The machine-readable classification.</summary>
    public string Code { get; }
}

/// <summary>A model-request failure, carrying the provider-neutral facts alongside the message.</summary>
public sealed class LlmError : HarnessError
{
    /// <param name="failure">The provider-neutral failure facts.</param>
    /// <param name="innerException">The failure this one wraps, when there is one.</param>
    public LlmError(LlmFailure failure, Exception? innerException = null)
        : base(failure.Message, failure.Code, innerException)
        => Failure = failure;

    /// <param name="message">Human-readable description.</param>
    /// <param name="code">Machine-readable classification.</param>
    /// <param name="innerException">The failure this one wraps, when there is one.</param>
    public LlmError(string message, string code, Exception? innerException = null)
        : this(new LlmFailure(message, code), innerException)
    {
    }

    /// <summary>The provider-neutral failure facts.</summary>
    public LlmFailure Failure { get; }
}

/// <summary>
/// The failure codes the harness itself branches on. Providers may report others;
/// anything unrecognized is treated as terminal.
/// </summary>
public static class LlmErrorCodes
{
    /// <summary>The request would exceed the model's context window.</summary>
    public const string ContextWindowExceeded = "CONTEXT_WINDOW_EXCEEDED";

    /// <summary>The account is out of quota or credit.</summary>
    public const string QuotaExceeded = "QUOTA";

    /// <summary>The provider completed the request but produced no content.</summary>
    public const string EmptyResponse = "EMPTY_RESPONSE";

    /// <summary>No usable credential was found for the route.</summary>
    public const string MissingCredential = "MISSING_CREDENTIAL";

    /// <summary>A credential was found but cannot be sent as-is.</summary>
    public const string InvalidCredential = "INVALID_CREDENTIAL";

    /// <summary>No adapter is registered for the requested provider route.</summary>
    public const string NoAdapter = "NO_ADAPTER";

    /// <summary>The provider is rate-limiting the caller.</summary>
    public const string RateLimit = "RATE_LIMIT";

    /// <summary>The provider returned a server-side failure.</summary>
    public const string Server = "SERVER";

    /// <summary>The request never reached the provider, or the connection broke.</summary>
    public const string Transport = "TRANSPORT";

    /// <summary>The stream went idle past its deadline.</summary>
    public const string Timeout = "TIMEOUT";

    /// <summary>The stream ended before the provider signalled completion.</summary>
    public const string StreamClosed = "STREAM_CLOSED";

    /// <summary>A streamed payload could not be parsed.</summary>
    public const string MalformedResponse = "MALFORMED_RESPONSE";

    /// <summary>The caller cancelled the request.</summary>
    public const string Aborted = "ABORTED";

    /// <summary>The request is malformed and will fail the same way if retried.</summary>
    public const string InvalidRequest = "INVALID_REQUEST";

    /// <summary>The content cannot be sent to this route, for example an image to a text-only model.</summary>
    public const string UnsupportedContent = "UNSUPPORTED_CONTENT";

    /// <summary>The route does not offer the requested thinking effort.</summary>
    public const string UnsupportedReasoningEffort = "UNSUPPORTED_REASONING_EFFORT";

    /// <summary>Nothing classified it; treated as terminal.</summary>
    public const string Unknown = "UNKNOWN";
}

/// <summary>
/// Classifies provider text against the two conditions the harness must recognize
/// whatever wording a provider uses, because each drives a different recovery.
/// </summary>
public static class FailureClassifier
{
    private static readonly string[] ContextWindowMarkers =
    [
        "context length",
        "context window",
        "maximum context",
        "too many tokens",
        "prompt is too long",
        "reduce the length",
    ];

    private static readonly string[] QuotaMarkers =
    [
        "insufficient balance",
        "insufficient_quota",
        "exceeded your current quota",
        "out of credit",
        "billing",
        "payment required",
    ];

    /// <summary>
    /// Whether provider text describes an over-long request, which compaction can fix.
    /// </summary>
    /// <param name="detail">Provider code, type, and message joined together.</param>
    /// <returns>True when the text names a context-window overflow.</returns>
    public static bool IsContextWindowExceeded(string? detail) => Matches(detail, ContextWindowMarkers);

    /// <summary>
    /// Whether provider text describes exhausted quota, which retrying cannot fix.
    /// </summary>
    /// <param name="detail">Provider code, type, and message joined together.</param>
    /// <returns>True when the text names a quota or billing problem.</returns>
    public static bool IsQuotaExceeded(string? detail) => Matches(detail, QuotaMarkers);

    private static bool Matches(string? detail, string[] markers)
    {
        if (string.IsNullOrWhiteSpace(detail)) return false;
        foreach (var marker in markers)
        {
            if (detail.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}

/// <summary>Turns any thrown value into the harness's failure vocabulary.</summary>
public static class LlmFailures
{
    /// <summary>
    /// Normalize an exception into provider-neutral failure facts.
    /// </summary>
    /// <param name="error">The exception to classify.</param>
    /// <returns>
    /// The <see cref="LlmError" />'s own facts when it has them, a cancellation as
    /// <see cref="LlmErrorCodes.Aborted" />, and anything else flattened under
    /// <see cref="LlmErrorCodes.Unknown" />.
    /// </returns>
    public static LlmFailure Normalize(Exception error) => error switch
    {
        LlmError llm => llm.Failure,
        OperationCanceledException => new LlmFailure("request aborted", LlmErrorCodes.Aborted),
        HarnessError harness => new LlmFailure(harness.Message, harness.Code),
        _ => new LlmFailure(Cordis.ErrorChain.Describe(error), LlmErrorCodes.Unknown),
    };
}

namespace Dsh.Cordis;

/// <summary>Severity of one framework log record.</summary>
public enum LogLevel
{
    /// <summary>Detail useful only while tracing the framework itself.</summary>
    Debug,

    /// <summary>An ordinary lifecycle fact.</summary>
    Info,

    /// <summary>A contained failure: the caller continued, but something went wrong.</summary>
    Warn,

    /// <summary>A failure that ended the operation reporting it.</summary>
    Error,
}

/// <summary>
/// Where the framework reports contained failures. Dispatch modes that swallow a
/// listener's exception report it here instead, so a failing observer is visible
/// without being able to break the producer.
/// </summary>
public interface ILogger
{
    /// <summary>
    /// Record one message.
    /// </summary>
    /// <param name="level">Severity of the record.</param>
    /// <param name="scope">Component reporting it, for example an event name.</param>
    /// <param name="message">Human-readable description.</param>
    /// <param name="error">The failure being reported, when there is one.</param>
    void Log(LogLevel level, string scope, string message, Exception? error = null);
}

/// <summary>A logger that discards every record — the default for tests and embedded hosts.</summary>
public sealed class NullLogger : ILogger
{
    /// <summary>The shared instance.</summary>
    public static NullLogger Instance { get; } = new();

    private NullLogger() { }

    /// <inheritdoc />
    public void Log(LogLevel level, string scope, string message, Exception? error = null) { }
}

/// <summary>A logger that writes one line per record to a text writer, defaulting to standard error.</summary>
public sealed class ConsoleLogger : ILogger
{
    private readonly TextWriter _writer;
    private readonly LogLevel _minimum;
    private readonly object _gate = new();

    /// <param name="writer">Where lines are written; standard error when omitted.</param>
    /// <param name="minimum">Records below this level are dropped.</param>
    public ConsoleLogger(TextWriter? writer = null, LogLevel minimum = LogLevel.Warn)
    {
        _writer = writer ?? Console.Error;
        _minimum = minimum;
    }

    /// <inheritdoc />
    public void Log(LogLevel level, string scope, string message, Exception? error = null)
    {
        if (level < _minimum) return;
        var line = error is null
            ? $"[{level.ToString().ToUpperInvariant()}] {scope}: {message}"
            : $"[{level.ToString().ToUpperInvariant()}] {scope}: {message} -- {ErrorChain.Describe(error)}";
        lock (_gate) _writer.WriteLine(line);
    }
}

/// <summary>Renders an exception and its causes as one line, mirroring the harness's `errorChain`.</summary>
public static class ErrorChain
{
    /// <summary>
    /// Flatten an exception and its inner chain into a single readable string.
    /// </summary>
    /// <param name="error">The exception to describe; a null value reads as "unknown error".</param>
    /// <returns>Each link's message joined by ": ", outermost first.</returns>
    public static string Describe(Exception? error)
    {
        if (error is null) return "unknown error";
        var parts = new List<string>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        for (var current = error; current is not null && seen.Add(current); current = current.InnerException)
        {
            var message = current.Message;
            if (!string.IsNullOrWhiteSpace(message)) parts.Add(message);
            if (current is AggregateException aggregate && aggregate.InnerExceptions.Count > 1)
            {
                parts.Add($"({aggregate.InnerExceptions.Count} inner failures)");
                break;
            }
        }

        return parts.Count == 0 ? error.GetType().Name : string.Join(": ", parts);
    }
}

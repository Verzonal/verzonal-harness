using System.Runtime.CompilerServices;
using System.Text;

namespace Dsh.Llm.DeepSeek;

/// <summary>
/// Reads a server-sent-event stream.
/// </summary>
/// <remarks>
/// Deliberately strict about framing: an event is dispatched only when its
/// blank-line terminator arrives, so a partial event left at the end of a broken
/// connection is treated as truncation rather than being flushed as if it were
/// complete. The provider's <c>[DONE]</c> sentinel is what marks a stream finished;
/// reaching the end without it means the response was cut short, and a model call
/// that was cut short cannot be trusted.
/// </remarks>
public static class SseReader
{
    /// <summary>The payload a provider sends after its last chunk.</summary>
    public const string Done = "[DONE]";

    /// <summary>
    /// Read one stream's event payloads.
    /// </summary>
    /// <param name="stream">The raw response body.</param>
    /// <param name="onComment">
    /// Receives keep-alive comments. They never become payloads, but they are proof
    /// the connection is alive, which is what lets an idle watchdog stay armed without
    /// tripping on a slow model.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Each event's data, in order, ending with the sentinel.</returns>
    /// <exception cref="LlmError">The stream ended without the sentinel.</exception>
    public static async IAsyncEnumerable<string> ReadAsync(
        Stream stream,
        Action<string>? onComment = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        var sawData = false;

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;

            if (line.Length == 0)
            {
                if (!sawData) continue;

                var payload = data.ToString();
                data.Clear();
                sawData = false;

                yield return payload;
                if (string.Equals(payload, Done, StringComparison.Ordinal)) yield break;
                continue;
            }

            if (line[0] == ':')
            {
                onComment?.Invoke(line[1..].TrimStart());
                continue;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            var field = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? string.Empty : line[(separator + 1)..];
            if (value.StartsWith(' ')) value = value[1..];

            if (!string.Equals(field, "data", StringComparison.Ordinal)) continue;

            // Multiple data fields in one event join with newlines, per the SSE spec.
            if (sawData) data.Append('\n');
            data.Append(value);
            sawData = true;
        }

        throw new LlmError("the model stream ended without its completion marker", LlmErrorCodes.StreamClosed);
    }
}

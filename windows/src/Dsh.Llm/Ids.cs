using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Llm;

/// <summary>
/// Identifies one message. Opaque across every boundary that carries it: nothing
/// parses it, and two ids are the same message only when the strings match.
/// </summary>
/// <param name="Value">The raw id string.</param>
[JsonConverter(typeof(MessageIdConverter))]
public readonly record struct MessageId(string Value)
{
    /// <summary>Mint a fresh identifier.</summary>
    /// <returns>A new random message id.</returns>
    public static MessageId New() => new(Guid.NewGuid().ToString("D"));

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Pairs one tool call with its result. Minted by the model, so it is never parsed
/// or assumed to have a shape.
/// </summary>
/// <param name="Value">The raw id string exactly as the provider produced it.</param>
[JsonConverter(typeof(CallIdConverter))]
public readonly record struct CallId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// A provider's own identifier for one request, carried on failures so a report can
/// be matched against provider-side logs.
/// </summary>
/// <param name="Value">The raw id string.</param>
[JsonConverter(typeof(ProviderRequestIdConverter))]
public readonly record struct ProviderRequestId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// One thinking-effort level a model advertises, such as <c>off</c> or <c>high</c>.
/// The set is provider-owned, so the harness never enumerates it.
/// </summary>
/// <param name="Value">The raw effort id.</param>
[JsonConverter(typeof(ReasoningEffortIdConverter))]
public readonly record struct ReasoningEffortId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Identifies one session, and its persistence artifacts.</summary>
/// <param name="Value">The raw session id.</param>
[JsonConverter(typeof(SessionIdConverter))]
public readonly record struct SessionId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Reads and writes <see cref="MessageId" /> as a plain JSON string.</summary>
public sealed class MessageIdConverter : JsonConverter<MessageId>
{
    /// <inheritdoc />
    public override MessageId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, MessageId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

/// <summary>Reads and writes <see cref="CallId" /> as a plain JSON string.</summary>
public sealed class CallIdConverter : JsonConverter<CallId>
{
    /// <inheritdoc />
    public override CallId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, CallId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

/// <summary>Reads and writes <see cref="ProviderRequestId" /> as a plain JSON string.</summary>
public sealed class ProviderRequestIdConverter : JsonConverter<ProviderRequestId>
{
    /// <inheritdoc />
    public override ProviderRequestId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ProviderRequestId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

/// <summary>Reads and writes <see cref="ReasoningEffortId" /> as a plain JSON string.</summary>
public sealed class ReasoningEffortIdConverter : JsonConverter<ReasoningEffortId>
{
    /// <inheritdoc />
    public override ReasoningEffortId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ReasoningEffortId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

/// <summary>Reads and writes <see cref="SessionId" /> as a plain JSON string.</summary>
public sealed class SessionIdConverter : JsonConverter<SessionId>
{
    /// <inheritdoc />
    public override SessionId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, SessionId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Session.Persistence;

/// <summary>
/// Reads and writes session events as JSON lines.
/// </summary>
/// <remarks>
/// The event's <c>type</c> selects which payload type to deserialize, which is what
/// lets the vocabulary grow without this codec knowing every producer. A type it does
/// not recognize is refused unless the event marks itself skippable — reconstructing
/// a session while silently dropping events that shape it would be a wrong read
/// presented as a successful one.
/// </remarks>
public sealed class SessionEventConverter : JsonConverter<SessionEvent>
{
    /// <inheritdoc />
    public override SessionEvent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        var type = root.GetProperty("type").GetString()
                   ?? throw new JsonException("session event has no type");
        var seq = root.GetProperty("seq").GetInt32();
        var time = root.GetProperty("time").GetInt64();
        var ignorable = root.TryGetProperty("ignorable", out var flag) && flag.GetBoolean();

        var payloadType = SessionEventRegistry.PayloadType(type);
        if (payloadType is null && !ignorable)
        {
            throw new UnknownSessionEventException(type);
        }

        object data;
        if (payloadType is null)
        {
            data = root.TryGetProperty("data", out var raw) ? JsonValue.From(raw.Clone()) : JsonValue.Null;
        }
        else
        {
            data = root.TryGetProperty("data", out var raw)
                ? JsonSerializer.Deserialize(raw.GetRawText(), payloadType, options)
                  ?? throw new JsonException($"session event \"{type}\" has an unreadable payload")
                : throw new JsonException($"session event \"{type}\" has no payload");
        }

        SurfaceOp? surfaceOp = null;
        if (root.TryGetProperty("surfaceOp", out var op))
        {
            surfaceOp = op.ValueKind == JsonValueKind.String
                ? AppendOp.Instance
                : new ReplaceOp(op.GetProperty("start").GetInt32(), op.GetProperty("end").GetInt32());
        }

        int[]? sources = null;
        if (root.TryGetProperty("sourceEventSeqs", out var cited))
        {
            sources = [.. cited.EnumerateArray().Select(static entry => entry.GetInt32())];
        }

        return new SessionEvent
        {
            Type = type,
            Seq = seq,
            Time = time,
            Data = data,
            Ignorable = ignorable,
            SurfaceOp = surfaceOp,
            SourceEventSeqs = sources,
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, SessionEvent value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", value.Type);
        writer.WriteNumber("seq", value.Seq);
        writer.WriteNumber("time", value.Time);

        writer.WritePropertyName("data");
        JsonSerializer.Serialize(writer, value.Data, value.Data.GetType(), options);

        if (value.Ignorable) writer.WriteBoolean("ignorable", true);

        switch (value.SurfaceOp)
        {
            case AppendOp:
                writer.WriteString("surfaceOp", "append");
                break;
            case ReplaceOp replace:
                writer.WritePropertyName("surfaceOp");
                writer.WriteStartObject();
                writer.WriteString("op", "replace");
                writer.WriteNumber("start", replace.Start);
                writer.WriteNumber("end", replace.End);
                writer.WriteEndObject();
                break;
            default:
                break;
        }

        if (value.SourceEventSeqs is { } sources)
        {
            writer.WritePropertyName("sourceEventSeqs");
            writer.WriteStartArray();
            foreach (var seq in sources) writer.WriteNumberValue(seq);
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }
}

/// <summary>
/// A log naming an event type this build does not know.
/// </summary>
/// <remarks>
/// Distinct from a parse failure on purpose. A reader tolerates a half-written last
/// line, because that is an interrupted run; it must not tolerate this, because an
/// unrecognized event may change how everything around it should be read, and
/// skipping it would present a wrong reconstruction as a successful one.
/// </remarks>
public sealed class UnknownSessionEventException : JsonException
{
    /// <param name="type">The event type that was not recognized.</param>
    public UnknownSessionEventException(string type)
        : base($"session log contains unrecognized required event \"{type}\"; refusing to reconstruct it")
        => EventType = type;

    /// <summary>The event type that was not recognized.</summary>
    public string EventType { get; }
}

/// <summary>The serializer settings every durable session artifact is written with.</summary>
public static class SessionJson
{
    /// <summary>
    /// The shared options: compact output, string enums, and the event codec.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = Build();

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new SessionEventConverter());
        return options;
    }

    /// <summary>
    /// Serialize one event as a single line.
    /// </summary>
    /// <param name="entry">The event to write.</param>
    /// <returns>Its JSON, with no newline.</returns>
    public static string Line(SessionEvent entry) => JsonSerializer.Serialize(entry, Options);

    /// <summary>
    /// Read one event back.
    /// </summary>
    /// <param name="line">One line of a session log.</param>
    /// <returns>The event.</returns>
    /// <exception cref="JsonException">The line is malformed or names an unrecognized required event.</exception>
    public static SessionEvent Parse(string line)
        => JsonSerializer.Deserialize<SessionEvent>(line, Options)
           ?? throw new JsonException("session log line is empty");
}

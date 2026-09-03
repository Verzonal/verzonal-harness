using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Session;

/// <summary>
/// A value that survives a round trip through the durable log unchanged.
/// </summary>
/// <remarks>
/// The log stores tool-private payloads whose shape only the producing tool knows.
/// Rather than storing arbitrary objects and discovering at write time that
/// something cannot be represented, those values are converted here — at the
/// boundary where they enter the log — so a payload that could not be replayed is
/// rejected at its source instead of corrupting a session.
/// </remarks>
[JsonConverter(typeof(JsonValueConverter))]
public abstract record JsonValue
{
    /// <summary>The JSON null literal.</summary>
    public static JsonValue Null { get; } = new JsonNull();

    /// <summary>
    /// Validate and copy one value into the lossless domain in a single pass.
    /// </summary>
    /// <param name="value">The value to convert.</param>
    /// <returns>An immutable copy that the log can store and replay.</returns>
    /// <exception cref="ArgumentException">
    /// The value is outside the domain — a non-finite or negative-zero number, a
    /// cycle, or an object type with no JSON representation.
    /// </exception>
    /// <remarks>
    /// Validation and copying happen together so a mutable source cannot present one
    /// value to the check and another to storage.
    /// </remarks>
    public static JsonValue From(object? value) => Convert(value, []);

    private static JsonValue Convert(object? value, HashSet<object> visiting)
    {
        switch (value)
        {
            case null:
                return Null;
            case JsonValue already:
                return already;
            case bool flag:
                return new JsonBool(flag);
            case string text:
                return new JsonString(text);
            case double number:
                return Number(number);
            case float number:
                return Number(number);
            case decimal number:
                return Number((double)number);
            case sbyte or byte or short or ushort or int or uint or long:
                return Number(System.Convert.ToDouble(value, CultureInfo.InvariantCulture));
            case ulong number:
                return Number(number);
            case JsonElement element:
                return FromElement(element);
            default:
                break;
        }

        if (!visiting.Add(value))
        {
            throw new ArgumentException("value contains a cycle and cannot be stored in the session log", nameof(value));
        }

        try
        {
            switch (value)
            {
                case IDictionary<string, object?> map:
                {
                    var entries = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
                    foreach (var (key, entry) in map) entries[key] = Convert(entry, visiting);
                    return new JsonObject(entries);
                }

                case IEnumerable<KeyValuePair<string, JsonValue>> typedMap:
                {
                    var entries = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
                    foreach (var (key, entry) in typedMap) entries[key] = entry;
                    return new JsonObject(entries);
                }

                case IEnumerable sequence:
                {
                    var items = new List<JsonValue>();
                    foreach (var item in sequence) items.Add(Convert(item, visiting));
                    return new JsonArray(items);
                }

                default:
                    throw new ArgumentException(
                        $"value of type {value.GetType().Name} has no JSON representation and cannot be stored in the session log",
                        nameof(value));
            }
        }
        finally
        {
            visiting.Remove(value);
        }
    }

    private static JsonValue Number(double number)
    {
        if (double.IsNaN(number) || double.IsInfinity(number))
        {
            throw new ArgumentException("a non-finite number cannot be stored in the session log", nameof(number));
        }

        // Negative zero round-trips as positive zero through JSON, so it is
        // normalized here rather than silently changing on reload.
        return new JsonNumber(number == 0 ? 0 : number);
    }

    private static JsonValue FromElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => Null,
        JsonValueKind.True => new JsonBool(true),
        JsonValueKind.False => new JsonBool(false),
        JsonValueKind.String => new JsonString(element.GetString() ?? string.Empty),
        JsonValueKind.Number => Number(element.GetDouble()),
        JsonValueKind.Array => new JsonArray([.. element.EnumerateArray().Select(FromElement)]),
        JsonValueKind.Object => new JsonObject(element.EnumerateObject()
            .ToDictionary(static property => property.Name, static property => FromElement(property.Value), StringComparer.Ordinal)),
        _ => throw new ArgumentException($"unsupported JSON value kind {element.ValueKind}", nameof(element)),
    };
}

/// <summary>The JSON null literal.</summary>
public sealed record JsonNull : JsonValue;

/// <summary>A JSON boolean.</summary>
/// <param name="Value">The boolean.</param>
public sealed record JsonBool(bool Value) : JsonValue;

/// <summary>A finite JSON number.</summary>
/// <param name="Value">The number.</param>
public sealed record JsonNumber(double Value) : JsonValue;

/// <summary>A JSON string.</summary>
/// <param name="Value">The string.</param>
public sealed record JsonString(string Value) : JsonValue;

/// <summary>A JSON array.</summary>
/// <param name="Items">The ordered items.</param>
public sealed record JsonArray(IReadOnlyList<JsonValue> Items) : JsonValue
{
    /// <inheritdoc />
    public bool Equals(JsonArray? other) => other is not null && Items.SequenceEqual(other.Items);

    /// <inheritdoc />
    public override int GetHashCode() => Items.Count;
}

/// <summary>A JSON object.</summary>
/// <param name="Entries">The properties, by name.</param>
public sealed record JsonObject(IReadOnlyDictionary<string, JsonValue> Entries) : JsonValue
{
    /// <summary>
    /// Read one property.
    /// </summary>
    /// <param name="name">The property name.</param>
    /// <returns>The value, or null when the object has no such property.</returns>
    public JsonValue? Get(string name) => Entries.GetValueOrDefault(name);

    /// <inheritdoc />
    public bool Equals(JsonObject? other)
    {
        if (other is null || Entries.Count != other.Entries.Count) return false;
        foreach (var (key, value) in Entries)
        {
            if (!other.Entries.TryGetValue(key, out var theirs) || !value.Equals(theirs)) return false;
        }

        return true;
    }

    /// <inheritdoc />
    public override int GetHashCode() => Entries.Count;
}

/// <summary>Reads and writes <see cref="JsonValue" /> as ordinary JSON.</summary>
public sealed class JsonValueConverter : JsonConverter<JsonValue>
{
    /// <inheritdoc />
    public override JsonValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => JsonValue.From(JsonElement.ParseValue(ref reader));

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, JsonValue value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case JsonNull:
                writer.WriteNullValue();
                break;
            case JsonBool flag:
                writer.WriteBooleanValue(flag.Value);
                break;
            case JsonNumber number:
                writer.WriteNumberValue(number.Value);
                break;
            case JsonString text:
                writer.WriteStringValue(text.Value);
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array.Items) Write(writer, item, options);
                writer.WriteEndArray();
                break;
            case JsonObject map:
                writer.WriteStartObject();
                foreach (var (key, entry) in map.Entries)
                {
                    writer.WritePropertyName(key);
                    Write(writer, entry, options);
                }

                writer.WriteEndObject();
                break;
            default:
                throw new JsonException($"unsupported JSON value {value.GetType().Name}");
        }
    }
}

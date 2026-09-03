using Dsh.Session;

namespace Dsh.Tools;

/// <summary>
/// A JSON Schema node, held in the form it is sent to the model.
/// </summary>
/// <remarks>
/// Only a fixed subset of JSON Schema is accepted, and anything outside it is
/// rejected when the tool registers rather than being silently unenforced. A schema
/// the harness cannot check is worse than no schema: the model would be told a
/// constraint exists that nothing verifies.
/// </remarks>
/// <param name="Raw">The schema's properties, exactly as they go on the wire.</param>
public sealed record JsonSchemaNode(IReadOnlyDictionary<string, object?> Raw)
{
    /// <summary>Keywords the validator understands and enforces.</summary>
    private static readonly HashSet<string> SupportedKeywords = new(StringComparer.Ordinal)
    {
        "type", "oneOf", "properties", "required", "additionalProperties", "items",
        "enum", "const", "description", "title", "default", "examples",
    };

    private static readonly HashSet<string> SupportedTypes = new(StringComparer.Ordinal)
    {
        "object", "array", "string", "number", "integer", "boolean", "null",
    };

    /// <summary>
    /// Reject a schema this build could not enforce.
    /// </summary>
    /// <exception cref="InvalidOperationException">The schema uses an unsupported keyword or type.</exception>
    public void AssertSupported()
    {
        foreach (var (keyword, value) in Raw)
        {
            if (!SupportedKeywords.Contains(keyword))
            {
                throw new InvalidOperationException(
                    $"tool schema uses unsupported keyword \"{keyword}\"; this build can only enforce: {string.Join(", ", SupportedKeywords.Order(StringComparer.Ordinal))}");
            }

            switch (keyword)
            {
                case "type" when value is string type && !SupportedTypes.Contains(type):
                    throw new InvalidOperationException($"tool schema uses unsupported type \"{type}\"");
                case "properties" when value is IReadOnlyDictionary<string, object?> properties:
                    foreach (var property in properties.Values) AsNode(property)?.AssertSupported();
                    break;
                case "items":
                    AsNode(value)?.AssertSupported();
                    break;
                case "oneOf" when value is IEnumerable<object?> branches:
                {
                    var count = 0;
                    foreach (var branch in branches)
                    {
                        AsNode(branch)?.AssertSupported();
                        count++;
                    }

                    if (count < 2) throw new InvalidOperationException("tool schema oneOf needs at least two branches");
                    break;
                }

                default:
                    break;
            }
        }
    }

    private static JsonSchemaNode? AsNode(object? value) => value switch
    {
        JsonSchemaNode node => node,
        IReadOnlyDictionary<string, object?> raw => new JsonSchemaNode(raw),
        _ => null,
    };

    /// <summary>
    /// The schema as a plain dictionary tree, for sending to the model.
    /// </summary>
    /// <returns>The schema with every nested node flattened to dictionaries.</returns>
    public IReadOnlyDictionary<string, object?> ToWire()
    {
        var wire = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (keyword, value) in Raw) wire[keyword] = FlattenValue(value);
        return wire;
    }

    private static object? FlattenValue(object? value) => value switch
    {
        JsonSchemaNode node => node.ToWire(),
        IReadOnlyDictionary<string, object?> raw => new JsonSchemaNode(raw).ToWire(),
        IReadOnlyList<object?> items => items.Select(FlattenValue).ToArray(),
        _ => value,
    };

    /// <summary>The node's declared type, when it declares one.</summary>
    public string? Type => Raw.GetValueOrDefault("type") as string;
}

/// <summary>Builders for the accepted JSON Schema subset.</summary>
public static class Schema
{
    /// <summary>
    /// One property of an object schema.
    /// </summary>
    /// <param name="Name">The property name.</param>
    /// <param name="Node">Its schema.</param>
    /// <param name="Required">Whether the model must supply it.</param>
    public sealed record Property(string Name, JsonSchemaNode Node, bool Required = false);

    /// <summary>
    /// An object schema.
    /// </summary>
    /// <param name="properties">Its properties.</param>
    /// <returns>The schema node.</returns>
    public static JsonSchemaNode Object(params Property[] properties)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        var required = new List<object?>();
        foreach (var property in properties)
        {
            map[property.Name] = property.Node;
            if (property.Required) required.Add(property.Name);
        }

        var raw = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "object",
            ["properties"] = map,
        };
        if (required.Count > 0) raw["required"] = required;
        return new JsonSchemaNode(raw);
    }

    /// <summary>
    /// A string schema.
    /// </summary>
    /// <param name="description">What the value means, written for the model.</param>
    /// <param name="allowed">The permitted values, when the set is closed.</param>
    /// <returns>The schema node.</returns>
    public static JsonSchemaNode String(string description, IReadOnlyList<string>? allowed = null)
    {
        var raw = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "string",
            ["description"] = description,
        };
        if (allowed is { Count: > 0 }) raw["enum"] = allowed.Cast<object?>().ToArray();
        return new JsonSchemaNode(raw);
    }

    /// <summary>
    /// A number schema.
    /// </summary>
    /// <param name="description">What the value means, written for the model.</param>
    /// <returns>The schema node.</returns>
    public static JsonSchemaNode Number(string description)
        => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "number",
            ["description"] = description,
        });

    /// <summary>
    /// A boolean schema.
    /// </summary>
    /// <param name="description">What the value means, written for the model.</param>
    /// <returns>The schema node.</returns>
    public static JsonSchemaNode Boolean(string description)
        => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "boolean",
            ["description"] = description,
        });

    /// <summary>
    /// An array schema.
    /// </summary>
    /// <param name="items">The element schema.</param>
    /// <param name="description">What the array means, written for the model.</param>
    /// <returns>The schema node.</returns>
    public static JsonSchemaNode Array(JsonSchemaNode items, string description)
        => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "array",
            ["description"] = description,
            ["items"] = items,
        });

    /// <summary>An object schema with no declared properties, for a tool that takes no arguments.</summary>
    /// <returns>The schema node.</returns>
    public static JsonSchemaNode EmptyObject()
        => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal),
        });

    /// <summary>A schema accepting any JSON value, for an output a tool shapes itself.</summary>
    /// <returns>The schema node.</returns>
    public static JsonSchemaNode Any() => new(new Dictionary<string, object?>(StringComparer.Ordinal));
}

/// <summary>Checks a value against the accepted JSON Schema subset.</summary>
public static class JsonSchemaValidator
{
    /// <summary>
    /// Validate one value.
    /// </summary>
    /// <param name="schema">The schema to check against.</param>
    /// <param name="value">The value to check.</param>
    /// <returns>Every violation found, empty when the value conforms.</returns>
    public static IReadOnlyList<string> Validate(JsonSchemaNode schema, JsonValue value)
    {
        var violations = new List<string>();
        Check(schema, value, "$", violations);
        return violations;
    }

    private static void Check(JsonSchemaNode schema, JsonValue value, string path, List<string> violations)
    {
        if (schema.Raw.TryGetValue("const", out var constant) && !Matches(constant, value))
        {
            violations.Add($"{path}: expected the constant value");
        }

        if (schema.Raw.TryGetValue("enum", out var allowed) && allowed is IEnumerable<object?> options)
        {
            var matched = options.Any(option => Matches(option, value));
            if (!matched) violations.Add($"{path}: value is not one of the permitted options");
        }

        if (schema.Raw.TryGetValue("oneOf", out var branches) && branches is IEnumerable<object?> variants)
        {
            var matches = 0;
            foreach (var branch in variants)
            {
                var node = branch as JsonSchemaNode
                           ?? (branch is IReadOnlyDictionary<string, object?> raw ? new JsonSchemaNode(raw) : null);
                if (node is null) continue;
                if (Validate(node, value).Count == 0) matches++;
            }

            if (matches != 1) violations.Add($"{path}: value must match exactly one variant, matched {matches}");
        }

        if (schema.Type is not { } type) return;

        switch (type)
        {
            case "object":
                CheckObject(schema, value, path, violations);
                break;
            case "array":
                CheckArray(schema, value, path, violations);
                break;
            case "string" when value is not JsonString:
                violations.Add($"{path}: expected a string");
                break;
            case "boolean" when value is not JsonBool:
                violations.Add($"{path}: expected a boolean");
                break;
            case "number" when value is not JsonNumber:
                violations.Add($"{path}: expected a number");
                break;
            case "integer":
                if (value is not JsonNumber number) violations.Add($"{path}: expected an integer");
                else if (Math.Floor(number.Value) != number.Value) violations.Add($"{path}: expected an integer");
                break;
            case "null" when value is not JsonNull:
                violations.Add($"{path}: expected null");
                break;
            default:
                break;
        }
    }

    private static void CheckObject(JsonSchemaNode schema, JsonValue value, string path, List<string> violations)
    {
        if (value is not JsonObject map)
        {
            violations.Add($"{path}: expected an object");
            return;
        }

        var properties = new Dictionary<string, JsonSchemaNode>(StringComparer.Ordinal);
        if (schema.Raw.GetValueOrDefault("properties") is IReadOnlyDictionary<string, object?> declared)
        {
            foreach (var (name, node) in declared)
            {
                var child = node as JsonSchemaNode
                            ?? (node is IReadOnlyDictionary<string, object?> raw ? new JsonSchemaNode(raw) : null);
                if (child is not null) properties[name] = child;
            }
        }

        if (schema.Raw.GetValueOrDefault("required") is IEnumerable<object?> required)
        {
            foreach (var entry in required)
            {
                if (entry is not string name) continue;
                if (!map.Entries.ContainsKey(name)) violations.Add($"{path}: missing required property \"{name}\"");
            }
        }

        if (schema.Raw.GetValueOrDefault("additionalProperties") is false)
        {
            foreach (var name in map.Entries.Keys)
            {
                if (!properties.ContainsKey(name)) violations.Add($"{path}: unexpected property \"{name}\"");
            }
        }

        foreach (var (name, child) in properties)
        {
            if (map.Entries.TryGetValue(name, out var entry)) Check(child, entry, $"{path}.{name}", violations);
        }
    }

    private static void CheckArray(JsonSchemaNode schema, JsonValue value, string path, List<string> violations)
    {
        if (value is not JsonArray array)
        {
            violations.Add($"{path}: expected an array");
            return;
        }

        var itemSchema = schema.Raw.GetValueOrDefault("items") as JsonSchemaNode
                         ?? (schema.Raw.GetValueOrDefault("items") is IReadOnlyDictionary<string, object?> raw
                             ? new JsonSchemaNode(raw)
                             : null);
        if (itemSchema is null) return;

        for (var index = 0; index < array.Items.Count; index++)
        {
            Check(itemSchema, array.Items[index], $"{path}[{index}]", violations);
        }
    }

    private static bool Matches(object? expected, JsonValue actual) => actual switch
    {
        JsonString text => expected is string candidate && string.Equals(candidate, text.Value, StringComparison.Ordinal),
        JsonBool flag => expected is bool candidate && candidate == flag.Value,
        JsonNumber number => expected is not null
                             && double.TryParse(
                                 Convert.ToString(expected, System.Globalization.CultureInfo.InvariantCulture),
                                 System.Globalization.NumberStyles.Any,
                                 System.Globalization.CultureInfo.InvariantCulture,
                                 out var candidate)
                             && Math.Abs(candidate - number.Value) < double.Epsilon,
        JsonNull => expected is null,
        _ => false,
    };
}

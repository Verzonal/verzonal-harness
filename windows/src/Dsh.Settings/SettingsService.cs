using System.Text.RegularExpressions;
using Dsh.Cordis;
using Dsh.Util;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Dsh.Settings;

/// <summary>Event keys the settings capability publishes.</summary>
public static class SettingsKeys
{
    /// <summary>The context key <see cref="SettingsService" /> is published under.</summary>
    public const string Service = "settings";

    /// <summary>One namespace's section changed.</summary>
    public static EmitKey<string> Changed { get; } = new("settings/changed");
}

/// <summary>
/// The user's own settings, kept in one YAML document.
/// </summary>
/// <remarks>
/// A section is resolved over its composition defaults rather than replacing them,
/// so a user who sets one field does not silently lose every other. Only what they
/// actually set is written back, which keeps the document readable and makes it
/// obvious what they changed.
/// </remarks>
public sealed class SettingsService : Service
{
    private static readonly Regex LegalNamespace = new("^[a-z][a-z0-9-]*$", RegexOptions.Compiled);

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, object?> _document = new(StringComparer.Ordinal);

    /// <param name="ctx">The mounting plugin's context.</param>
    /// <param name="path">The document; <c>settings.yaml</c> in the harness home when omitted.</param>
    public SettingsService(Context ctx, string? path = null) : base(ctx, SettingsKeys.Service)
        => _path = path ?? HomePaths.Combine("settings.yaml");

    /// <summary>The document's path, for opening it in an editor.</summary>
    public string DocumentPath => _path;

    /// <inheritdoc />
    public override Task StartAsync()
    {
        Reload();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Re-read the document from disk.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document exists but is not valid YAML.</exception>
    public void Reload()
    {
        lock (_gate)
        {
            _document = ReadDocument();
        }
    }

    /// <summary>
    /// Read one namespace's raw section.
    /// </summary>
    /// <param name="ns">The namespace.</param>
    /// <returns>The section as read from the document, or an empty map when unset.</returns>
    public IReadOnlyDictionary<string, object?> Section(string ns)
    {
        AssertLegal(ns);
        lock (_gate)
        {
            return _document.TryGetValue(ns, out var section)
                ? Normalize(section)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Read one setting.
    /// </summary>
    /// <param name="ns">The namespace.</param>
    /// <param name="key">The field name.</param>
    /// <param name="fallback">What the composition would use when the user set nothing.</param>
    /// <returns>The user's value when they set one, else the fallback.</returns>
    public string Get(string ns, string key, string fallback)
        => Section(ns).TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? fallback
            : fallback;

    /// <summary>
    /// Read one boolean setting.
    /// </summary>
    /// <param name="ns">The namespace.</param>
    /// <param name="key">The field name.</param>
    /// <param name="fallback">What the composition would use when the user set nothing.</param>
    /// <returns>The user's value when they set one, else the fallback.</returns>
    public bool GetBool(string ns, string key, bool fallback)
        => Section(ns).TryGetValue(key, out var value) && value is not null
            && bool.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out var parsed)
                ? parsed
                : fallback;

    /// <summary>
    /// Merge fields into one namespace's section.
    /// </summary>
    /// <param name="ns">The namespace.</param>
    /// <param name="patch">The fields to set; a null value removes a field.</param>
    public void Update(string ns, IReadOnlyDictionary<string, object?> patch)
    {
        AssertLegal(ns);
        lock (_gate)
        {
            var section = _document.TryGetValue(ns, out var existing)
                ? Normalize(existing)
                : new Dictionary<string, object?>(StringComparer.Ordinal);

            foreach (var (key, value) in patch)
            {
                if (value is null) section.Remove(key);
                else section[key] = value;
            }

            if (section.Count == 0) _document.Remove(ns);
            else _document[ns] = section;

            WriteDocument();
        }

        Ctx.Emit(SettingsKeys.Changed, ns);
    }

    private static void AssertLegal(string ns)
    {
        if (!LegalNamespace.IsMatch(ns))
        {
            throw new InvalidOperationException(
                $"\"{ns}\" is not a legal settings namespace; use lowercase letters, digits, and hyphens");
        }
    }

    private Dictionary<string, object?> ReadDocument()
    {
        if (!File.Exists(_path)) return new Dictionary<string, object?>(StringComparer.Ordinal);

        try
        {
            var text = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(text)) return new Dictionary<string, object?>(StringComparer.Ordinal);

            var parsed = new DeserializerBuilder()
                .WithNamingConvention(NullNamingConvention.Instance)
                .Build()
                .Deserialize<Dictionary<string, object?>>(text);

            return parsed is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : new Dictionary<string, object?>(parsed, StringComparer.Ordinal);
        }
        catch (YamlDotNet.Core.YamlException error)
        {
            // Loud rather than silently empty: a settings file the user broke should
            // say so, not quietly revert them to defaults.
            throw new InvalidOperationException(
                $"{_path} is not valid YAML and cannot be read: {error.Message}",
                error);
        }
    }

    private void WriteDocument()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) AtomicFile.CreateOwnerOnlyDirectory(directory);

        AtomicFile.WriteAllText(
            _path,
            new SerializerBuilder().WithNamingConvention(NullNamingConvention.Instance).Build().Serialize(_document),
            ownerOnly: true);
    }

    /// <summary>
    /// Read a section into a uniform map.
    /// </summary>
    /// <param name="section">The stored section.</param>
    /// <returns>Its fields, or an empty map when it is not a mapping at all.</returns>
    /// <remarks>
    /// A section can arrive either as the parser's object-keyed mapping or as a map
    /// this service wrote earlier in the same process, so both shapes are accepted —
    /// otherwise a second update in one run would silently start from nothing and drop
    /// every field the first one set.
    /// </remarks>
    private static Dictionary<string, object?> Normalize(object? section)
    {
        var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);

        switch (section)
        {
            case IDictionary<string, object?> typed:
                foreach (var (name, value) in typed) normalized[name] = value;
                break;
            case IDictionary<object, object?> loose:
                foreach (var (key, value) in loose)
                {
                    if (key is string name) normalized[name] = value;
                }

                break;
            default:
                break;
        }

        return normalized;
    }

    /// <summary>Mount the settings capability.</summary>
    /// <param name="path">The document; <c>settings.yaml</c> in the harness home when omitted.</param>
    /// <returns>The plugin to hand to <see cref="Context.Plugin" />.</returns>
    public static IPlugin Plugin(string? path = null)
        => ServicePlugin.Create("settings-file", SettingsKeys.Service, ctx => new SettingsService(ctx, path));
}

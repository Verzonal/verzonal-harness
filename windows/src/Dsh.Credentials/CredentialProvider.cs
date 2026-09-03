using System.Text.RegularExpressions;
using Dsh.Cordis;
using Dsh.Util;
using YamlDotNet.Serialization;

namespace Dsh.Credentials;

/// <summary>A credential that was found, and where it came from.</summary>
/// <param name="Value">The secret itself.</param>
/// <param name="Source">Which layer supplied it, for showing a person without revealing the value.</param>
public sealed record ResolvedCredential(string Value, string Source);

/// <summary>What is known about a credential without revealing it.</summary>
/// <param name="Reference">The name it is stored under.</param>
/// <param name="Configured">Whether a value was found.</param>
/// <param name="Source">Which layer supplied it.</param>
/// <param name="Writable">Whether this harness can change it, or only read it.</param>
public sealed record CredentialInfo(string Reference, bool Configured, string? Source, bool Writable);

/// <summary>
/// The credential capability's Service Definition.
/// </summary>
/// <remarks>
/// Adapters ask for a credential by <em>name</em> and never hold the value, so a key
/// that changes on disk takes effect on the next request and no long-lived object
/// keeps a stale secret alive.
/// </remarks>
public abstract class CredentialProvider : Service
{
    /// <param name="ctx">The mounting plugin's context.</param>
    protected CredentialProvider(Context ctx) : base(ctx, CredentialKeys.Service) { }

    /// <summary>
    /// Find a credential.
    /// </summary>
    /// <param name="reference">The name it is stored under, such as an environment-variable name.</param>
    /// <returns>The value and its source, or null when nothing has it.</returns>
    public abstract ResolvedCredential? Resolve(string reference);

    /// <summary>
    /// Describe a credential without revealing it.
    /// </summary>
    /// <param name="reference">The name it is stored under.</param>
    /// <returns>Whether it is set, where from, and whether it can be changed here.</returns>
    public abstract CredentialInfo Describe(string reference);

    /// <summary>
    /// Store a credential in the managed document.
    /// </summary>
    /// <param name="reference">The name to store it under.</param>
    /// <param name="value">The secret.</param>
    public abstract void Set(string reference, string value);

    /// <summary>
    /// Remove a credential from the managed document.
    /// </summary>
    /// <param name="reference">The name to remove.</param>
    public abstract void Unset(string reference);
}

/// <summary>The context key the credential capability is published under.</summary>
public static class CredentialKeys
{
    /// <summary>The context key a credential provider claims.</summary>
    public const string Service = "credentials";
}

/// <summary>
/// Resolves credentials from the machine.
/// </summary>
/// <remarks>
/// Four layers, highest first: the process environment, the managed
/// <c>.credentials.yaml</c>, a project <c>.env</c>, and a user <c>.env</c>. The
/// environment wins so an operator can override anything the harness stored without
/// editing a file, and only the managed document is writable — the harness never
/// rewrites a file a person maintains by hand.
///
/// An empty stored value counts as absent everywhere, so clearing a key in one layer
/// does not accidentally mask a real value in a lower one by looking "set".
/// </remarks>
public sealed class LocalCredentials : CredentialProvider
{
    private static readonly Regex LegalReference = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>Printable ASCII with no spaces: what an HTTP header can carry unencoded.</summary>
    private static readonly Regex LegalValue = new("^[\\x21-\\x7E]+$", RegexOptions.Compiled);

    /// <summary>The file name of the document this provider manages.</summary>
    public const string FileName = ".credentials.yaml";

    /// <summary>The document format this build writes.</summary>
    public const int DocumentVersion = 1;

    private readonly string _documentPath;
    private readonly string? _projectDirectory;
    private readonly object _gate = new();

    /// <param name="ctx">The mounting plugin's context.</param>
    /// <param name="documentPath">The managed document; the harness home's when omitted.</param>
    /// <param name="projectDirectory">Where a project <c>.env</c> is looked for.</param>
    public LocalCredentials(Context ctx, string? documentPath = null, string? projectDirectory = null) : base(ctx)
    {
        _documentPath = documentPath ?? HomePaths.Combine(FileName);
        _projectDirectory = projectDirectory;
    }

    /// <summary>The managed document's path, for showing a person where their keys live.</summary>
    public string DocumentPath => _documentPath;

    /// <inheritdoc />
    public override ResolvedCredential? Resolve(string reference)
    {
        if (!LegalReference.IsMatch(reference)) return null;

        var fromEnvironment = Environment.GetEnvironmentVariable(reference);
        if (!string.IsNullOrEmpty(fromEnvironment)) return new ResolvedCredential(fromEnvironment, "env");

        if (ReadDocument().TryGetValue(reference, out var stored) && !string.IsNullOrEmpty(stored))
        {
            return new ResolvedCredential(stored, "file");
        }

        if (_projectDirectory is not null
            && ReadDotEnv(Path.Combine(_projectDirectory, ".env")).TryGetValue(reference, out var project)
            && !string.IsNullOrEmpty(project))
        {
            return new ResolvedCredential(project, "project-env");
        }

        if (ReadDotEnv(HomePaths.Combine(".env")).TryGetValue(reference, out var user)
            && !string.IsNullOrEmpty(user))
        {
            return new ResolvedCredential(user, "user-env");
        }

        return null;
    }

    /// <inheritdoc />
    public override CredentialInfo Describe(string reference)
    {
        var resolved = Resolve(reference);
        return new CredentialInfo(
            reference,
            resolved is not null,
            resolved?.Source,
            resolved is null || resolved.Source == "file");
    }

    /// <inheritdoc />
    public override void Set(string reference, string value)
    {
        if (!LegalReference.IsMatch(reference))
        {
            throw new InvalidOperationException(
                $"\"{reference}\" is not a legal credential name; use letters, digits, and underscores");
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException($"the value for {reference} is empty");
        }

        if (!LegalValue.IsMatch(trimmed))
        {
            // The value is never echoed: only the name it was stored under.
            throw new InvalidOperationException(
                $"the value for {reference} contains characters that cannot be sent in a request header");
        }

        lock (_gate)
        {
            var document = ReadDocument();
            document[reference] = trimmed;
            WriteDocument(document);
        }
    }

    /// <inheritdoc />
    public override void Unset(string reference)
    {
        lock (_gate)
        {
            var document = ReadDocument();
            if (!document.Remove(reference)) return;
            WriteDocument(document);
        }
    }

    private Dictionary<string, string> ReadDocument()
    {
        if (!File.Exists(_documentPath)) return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var yaml = new DeserializerBuilder().Build()
                .Deserialize<Dictionary<string, object?>>(File.ReadAllText(_documentPath));
            if (yaml is null) return new Dictionary<string, string>(StringComparer.Ordinal);

            var refs = new Dictionary<string, string>(StringComparer.Ordinal);
            if (yaml.TryGetValue("refs", out var section) && section is IDictionary<object, object?> entries)
            {
                foreach (var (key, value) in entries)
                {
                    if (key is string name && value is string text) refs[name] = text;
                }
            }

            return refs;
        }
        catch (YamlDotNet.Core.YamlException error)
        {
            // Loud rather than silently empty: a malformed credentials file would
            // otherwise look exactly like "no keys configured".
            throw new InvalidOperationException(
                $"{_documentPath} is not valid YAML and cannot be read: {error.Message}",
                error);
        }
    }

    private void WriteDocument(Dictionary<string, string> refs)
    {
        var directory = Path.GetDirectoryName(_documentPath);
        if (!string.IsNullOrEmpty(directory)) AtomicFile.CreateOwnerOnlyDirectory(directory);

        var document = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["version"] = DocumentVersion,
            ["refs"] = refs,
        };

        AtomicFile.WriteAllText(
            _documentPath,
            new SerializerBuilder().Build().Serialize(document),
            ownerOnly: true);
    }

    /// <summary>
    /// Read a <c>.env</c> file's assignments.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <returns>Its assignments, empty when the file is missing or unreadable.</returns>
    internal static Dictionary<string, string> ReadDotEnv(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return values;

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0) continue;

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2
                && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            if (LegalReference.IsMatch(name)) values[name] = value;
        }

        return values;
    }

    /// <summary>Mount the local credential provider.</summary>
    /// <param name="documentPath">The managed document; the harness home's when omitted.</param>
    /// <param name="projectDirectory">Where a project <c>.env</c> is looked for.</param>
    /// <returns>The plugin to hand to <see cref="Context.Plugin" />.</returns>
    public static IPlugin Plugin(string? documentPath = null, string? projectDirectory = null)
        => ServicePlugin.Create<CredentialProvider>(
            "credentials-local",
            CredentialKeys.Service,
            ctx => new LocalCredentials(ctx, documentPath, projectDirectory));
}

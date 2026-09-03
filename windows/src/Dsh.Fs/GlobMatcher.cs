using System.Text;
using System.Text.RegularExpressions;

namespace Dsh.Fs;

/// <summary>
/// Matches paths against a glob.
/// </summary>
/// <remarks>
/// A pattern with no separator matches the file's <em>name</em> at any depth, so
/// <c>*.cs</c> searches the whole tree rather than only the top directory — which is
/// what someone writing that pattern means. Including a separator anchors the depth,
/// so <c>src/*.cs</c> matches only directly inside <c>src</c>.
/// </remarks>
public sealed class GlobMatcher
{
    private readonly Regex _regex;
    private readonly bool _nameOnly;

    /// <param name="pattern">The glob to match against.</param>
    public GlobMatcher(string pattern)
    {
        _nameOnly = !pattern.Contains('/', StringComparison.Ordinal);
        _regex = new Regex(
            "^" + Translate(pattern) + "$",
            RegexOptions.CultureInvariant | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None));
    }

    /// <summary>
    /// Whether a path matches.
    /// </summary>
    /// <param name="relativePath">A workspace-relative path using forward slashes.</param>
    /// <returns>True when the glob matches it.</returns>
    public bool Matches(string relativePath)
    {
        var candidate = relativePath.Replace('\\', '/');
        if (_nameOnly)
        {
            var slash = candidate.LastIndexOf('/');
            if (slash >= 0) candidate = candidate[(slash + 1)..];
        }

        return _regex.IsMatch(candidate);
    }

    /// <summary>
    /// Convert a glob into the equivalent regular expression.
    /// </summary>
    /// <param name="pattern">The glob.</param>
    /// <returns>The regular-expression body, without anchors.</returns>
    internal static string Translate(string pattern)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            switch (character)
            {
                case '*':
                    if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                    {
                        // `**/` may also match nothing, so `**/x` finds `x` at the root.
                        if (index + 2 < pattern.Length && pattern[index + 2] == '/')
                        {
                            builder.Append("(?:.*/)?");
                            index += 2;
                        }
                        else
                        {
                            builder.Append(".*");
                            index++;
                        }
                    }
                    else
                    {
                        builder.Append("[^/]*");
                    }

                    break;
                case '?':
                    builder.Append("[^/]");
                    break;
                case '{':
                    builder.Append("(?:");
                    break;
                case '}':
                    builder.Append(')');
                    break;
                case ',':
                    builder.Append('|');
                    break;
                case '/':
                    builder.Append('/');
                    break;
                default:
                    builder.Append(Regex.Escape(character.ToString()));
                    break;
            }
        }

        return builder.ToString();
    }
}

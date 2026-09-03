using System.Globalization;
using System.Text;
using Dsh.Llm;

namespace Dsh.Session.Persistence;

/// <summary>
/// Where a session's log lives on disk.
/// </summary>
/// <remarks>
/// Sessions are grouped by the directory they were started in, so a person browsing
/// the store finds their project rather than a flat list of opaque ids. The project
/// folder's name is deliberately lossy — it exists to be recognizable — while the
/// session's own folder name is reversible, because two sessions must never collide
/// and an id from an untrusted source must never escape the store.
/// </remarks>
public static class SessionPaths
{
    private const int MaxProjectSlug = 251;

    /// <summary>The folder used when a session belongs to no directory.</summary>
    public const string NoWorkspaceFolder = "_no-cwd";

    /// <summary>The log's file name, uncompressed.</summary>
    public const string LogFileName = "session.jsonl";

    /// <summary>The log's file name as the Node harness compresses it.</summary>
    public const string CompressedLogFileName = "session.jsonl.zstd";

    /// <summary>
    /// The readable folder name for one workspace.
    /// </summary>
    /// <param name="workspace">The directory the session belongs to.</param>
    /// <returns>A folder name wrapped in double dashes, or the no-workspace folder.</returns>
    public static string ProjectFolder(string? workspace)
    {
        if (string.IsNullOrEmpty(workspace)) return NoWorkspaceFolder;

        var builder = new StringBuilder();
        foreach (var character in workspace)
        {
            if (character is '/' or '\\' or ':')
            {
                if (builder.Length > 0 && builder[^1] == '-') continue;
                builder.Append('-');
            }
            else if (IsSafe(character) && character != '~')
            {
                builder.Append(character);
            }
            else
            {
                builder.Append('~').Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
            }
        }

        var slug = builder.ToString().TrimStart('-');
        if (slug.Length == 0) slug = "root";
        if (slug.Length > MaxProjectSlug) slug = slug[..MaxProjectSlug];
        return $"--{slug}--";
    }

    /// <summary>
    /// The reversible folder name for one session id.
    /// </summary>
    /// <param name="id">The session id, which may be anything.</param>
    /// <returns>
    /// A folder name that maps one-to-one onto the id, so two ids never share a folder
    /// and no id can traverse out of the store.
    /// </returns>
    public static string EncodeSegment(string id)
    {
        var builder = new StringBuilder();
        foreach (var character in id)
        {
            if (IsSafe(character) && character != '~' && character != '.')
            {
                builder.Append(character);
            }
            else
            {
                builder.Append('~').Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
            }
        }

        return builder.Length == 0 ? "~0000" : builder.ToString();
    }

    /// <summary>
    /// Recover a session id from its folder name.
    /// </summary>
    /// <param name="segment">The folder name.</param>
    /// <returns>The original id.</returns>
    public static string DecodeSegment(string segment)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < segment.Length; index++)
        {
            if (segment[index] == '~' && index + 4 < segment.Length)
            {
                var hex = segment.Substring(index + 1, 4);
                if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                {
                    builder.Append((char)code);
                    index += 4;
                    continue;
                }
            }

            builder.Append(segment[index]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The directory holding one session's artifacts.
    /// </summary>
    /// <param name="root">The store's root.</param>
    /// <param name="workspace">The directory the session belongs to.</param>
    /// <param name="id">The session id.</param>
    /// <returns>The session's own directory.</returns>
    public static string SessionDirectory(string root, string? workspace, SessionId id)
        => Path.Combine(root, ProjectFolder(workspace), EncodeSegment(id.Value));

    /// <summary>
    /// The log file for one session.
    /// </summary>
    /// <param name="root">The store's root.</param>
    /// <param name="workspace">The directory the session belongs to.</param>
    /// <param name="id">The session id.</param>
    /// <returns>The log's path.</returns>
    public static string LogPath(string root, string? workspace, SessionId id)
        => Path.Combine(SessionDirectory(root, workspace, id), LogFileName);

    private static bool IsSafe(char character)
        => character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-' or '~';
}

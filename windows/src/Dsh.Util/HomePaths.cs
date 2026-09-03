namespace Dsh.Util;

/// <summary>
/// Where the harness keeps a user's data.
/// </summary>
/// <remarks>
/// One root holds everything — sessions, settings, credentials — so a user can find,
/// back up, or delete their harness state in one place. The layout matches what the
/// Node harness writes, so both can open the same sessions.
/// </remarks>
public static class HomePaths
{
    /// <summary>The directory name under the operating-system home.</summary>
    public const string DirectoryName = ".dsh";

    /// <summary>The environment variable that overrides the default location.</summary>
    public const string EnvironmentVariable = "DSH_HOME";

    /// <summary>
    /// Resolve the harness home.
    /// </summary>
    /// <param name="configured">An explicit path from configuration, which wins.</param>
    /// <returns>
    /// The configured path, else <c>DSH_HOME</c>, else <c>.dsh</c> under the user's
    /// home. A blank or whitespace-only environment value counts as unset.
    /// </returns>
    public static string Resolve(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(ExpandHome(configured));

        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return Path.GetFullPath(ExpandHome(fromEnvironment));

        return Path.Combine(UserHome(), DirectoryName);
    }

    /// <summary>
    /// A path inside the harness home.
    /// </summary>
    /// <param name="segments">Path segments to append.</param>
    /// <returns>The combined absolute path.</returns>
    public static string Combine(params string[] segments) => Path.Combine([Resolve(), .. segments]);

    /// <summary>
    /// Expand a leading tilde against the operating-system home.
    /// </summary>
    /// <param name="path">A path that may start with <c>~</c>.</param>
    /// <returns>The expanded path, or the original when it has no tilde prefix.</returns>
    public static string ExpandHome(string path)
    {
        if (path == "~") return UserHome();
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(UserHome(), path[2..]);
        }

        return path;
    }

    private static string UserHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? Directory.GetCurrentDirectory() : home;
    }
}

/// <summary>
/// Writes a file so a reader never sees it half-written.
/// </summary>
/// <remarks>
/// Session logs, settings, and credentials are all read by other processes while the
/// harness runs. Writing through a temporary file and replacing means a concurrent
/// reader sees either the old content or the new, never a truncated document.
/// </remarks>
public static class AtomicFile
{
    /// <summary>
    /// Replace a file's contents atomically.
    /// </summary>
    /// <param name="path">The file to write.</param>
    /// <param name="contents">The new contents.</param>
    /// <param name="ownerOnly">Restrict the file to its owner, for credentials and settings.</param>
    public static void WriteAllText(string path, string contents, bool ownerOnly = false)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temporary, contents);
        if (ownerOnly) RestrictToOwner(temporary);

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Append lines to a file, creating it and its directory when needed.
    /// </summary>
    /// <param name="path">The file to append to.</param>
    /// <param name="lines">The lines to add, each terminated with a newline.</param>
    public static void AppendLines(string path, IEnumerable<string> lines)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var writer = new StreamWriter(path, append: true);
        foreach (var line in lines) writer.Write(line + "\n");
    }

    /// <summary>
    /// Restrict a file to its owner where the platform supports it.
    /// </summary>
    /// <param name="path">The file to restrict.</param>
    /// <remarks>
    /// A no-op on Windows, where the inherited ACL already restricts a file under the
    /// user's profile and there is no mode bit to set.
    /// </remarks>
    public static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>
    /// Create a directory restricted to its owner where the platform supports it.
    /// </summary>
    /// <param name="path">The directory to create.</param>
    /// <remarks>
    /// Only a directory this call creates is restricted. One that already exists
    /// belongs to whoever made it: narrowing its mode could lock out its real owner,
    /// and on a shared parent such as <c>/tmp</c> the attempt fails outright.
    /// </remarks>
    public static void CreateOwnerOnlyDirectory(string path)
    {
        if (Directory.Exists(path)) return;

        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

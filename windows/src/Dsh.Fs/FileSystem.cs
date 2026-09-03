using System.Text;
using Dsh.Cordis;

namespace Dsh.Fs;

/// <summary>What is known about one filesystem entry.</summary>
/// <param name="Path">Its absolute path.</param>
/// <param name="IsDirectory">Whether it is a directory.</param>
/// <param name="Size">Its size in bytes; zero for a directory.</param>
/// <param name="ModifiedAt">When it was last written.</param>
public sealed record FileEntry(string Path, bool IsDirectory, long Size, DateTimeOffset ModifiedAt);

/// <summary>A filesystem operation that could not be carried out.</summary>
public sealed class FileSystemException : Exception
{
    /// <param name="message">What went wrong, written for the model.</param>
    /// <param name="code">The machine-readable classification.</param>
    /// <param name="innerException">The failure this one wraps.</param>
    public FileSystemException(string message, string code, Exception? innerException = null)
        : base(message, innerException)
        => Code = code;

    /// <summary>The machine-readable classification.</summary>
    public string Code { get; }
}

/// <summary>The failure codes filesystem operations report.</summary>
public static class FileSystemCodes
{
    /// <summary>Nothing exists at that path.</summary>
    public const string NotFound = "FS_NOT_FOUND";

    /// <summary>The path names a directory where a file was expected, or the reverse.</summary>
    public const string WrongKind = "FS_WRONG_KIND";

    /// <summary>The operating system refused access.</summary>
    public const string Denied = "FS_DENIED";

    /// <summary>The file is not text this harness can read.</summary>
    public const string NotText = "FS_NOT_TEXT";
}

/// <summary>
/// The filesystem capability's Service Definition.
/// </summary>
/// <remarks>
/// Pointing this seam somewhere else — a remote sandbox, a virtual workspace — moves
/// every file-touching tool with it, because none of them reach for the real
/// filesystem directly.
/// </remarks>
public abstract class FileSystemService : Service
{
    /// <param name="ctx">The mounting plugin's context.</param>
    protected FileSystemService(Context ctx) : base(ctx, FsKeys.Service) { }

    /// <summary>
    /// Resolve a possibly relative path against the workspace.
    /// </summary>
    /// <param name="path">The path as the model wrote it.</param>
    /// <returns>An absolute path.</returns>
    public abstract string Resolve(string path);

    /// <summary>
    /// Read a text file.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The file's text.</returns>
    /// <exception cref="FileSystemException">It is missing, unreadable, or not text.</exception>
    public abstract Task<string> ReadTextAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Create or replace a text file.
    /// </summary>
    /// <param name="path">The file to write.</param>
    /// <param name="contents">Its new contents.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task completing once the file is written.</returns>
    public abstract Task WriteTextAsync(string path, string contents, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether anything exists at a path.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <returns>True when a file or directory is there.</returns>
    public abstract bool Exists(string path);

    /// <summary>
    /// Describe one entry.
    /// </summary>
    /// <param name="path">The path to describe.</param>
    /// <returns>What is known about it, or null when nothing is there.</returns>
    public abstract FileEntry? Stat(string path);

    /// <summary>
    /// List a directory's immediate children.
    /// </summary>
    /// <param name="path">The directory to list.</param>
    /// <returns>Its entries.</returns>
    /// <exception cref="FileSystemException">It is missing or is not a directory.</exception>
    public abstract IReadOnlyList<FileEntry> List(string path);

    /// <summary>
    /// Find files whose paths match a glob.
    /// </summary>
    /// <param name="root">Where to search.</param>
    /// <param name="pattern">The glob to match.</param>
    /// <param name="limit">The most results to return.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>
    /// Matching files, newest first, and whether the walk found more than the limit.
    /// Directories never match: a tool asking for files should not have to filter them out.
    /// </returns>
    public abstract (IReadOnlyList<FileEntry> Files, bool Truncated) Glob(
        string root,
        string pattern,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>The context key the filesystem capability is published under.</summary>
public static class FsKeys
{
    /// <summary>The context key a filesystem provider claims.</summary>
    public const string Service = "fs";
}

/// <summary>
/// The filesystem of the machine the harness runs on.
/// </summary>
public sealed class LocalFileSystem : FileSystemService
{
    private readonly string _workspace;

    /// <param name="ctx">The mounting plugin's context.</param>
    /// <param name="workspace">The directory relative paths resolve against.</param>
    public LocalFileSystem(Context ctx, string workspace) : base(ctx)
        => _workspace = Path.GetFullPath(workspace);

    /// <summary>The directory relative paths resolve against.</summary>
    public string Workspace => _workspace;

    /// <inheritdoc />
    public override string Resolve(string path)
    {
        var expanded = Dsh.Util.HomePaths.ExpandHome(path);
        return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(_workspace, expanded));
    }

    /// <inheritdoc />
    public override async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
    {
        var full = Resolve(path);
        if (Directory.Exists(full))
        {
            throw new FileSystemException($"{path} is a directory, not a file", FileSystemCodes.WrongKind);
        }

        if (!File.Exists(full))
        {
            throw new FileSystemException($"{path} does not exist", FileSystemCodes.NotFound);
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(full, cancellationToken);
            if (LooksBinary(bytes))
            {
                throw new FileSystemException(
                    $"{path} is not a UTF-8 text file",
                    FileSystemCodes.NotText);
            }

            return new UTF8Encoding(false, false).GetString(bytes);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new FileSystemException($"{path} could not be read: access denied", FileSystemCodes.Denied, error);
        }
    }

    /// <inheritdoc />
    public override async Task WriteTextAsync(
        string path,
        string contents,
        CancellationToken cancellationToken = default)
    {
        var full = Resolve(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(full, contents, new UTF8Encoding(false), cancellationToken);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new FileSystemException($"{path} could not be written: access denied", FileSystemCodes.Denied, error);
        }
    }

    /// <inheritdoc />
    public override bool Exists(string path)
    {
        var full = Resolve(path);
        return File.Exists(full) || Directory.Exists(full);
    }

    /// <inheritdoc />
    public override FileEntry? Stat(string path)
    {
        var full = Resolve(path);
        if (Directory.Exists(full))
        {
            var directory = new DirectoryInfo(full);
            return new FileEntry(full, true, 0, directory.LastWriteTimeUtc);
        }

        if (!File.Exists(full)) return null;
        var file = new FileInfo(full);
        return new FileEntry(full, false, file.Length, file.LastWriteTimeUtc);
    }

    /// <inheritdoc />
    public override IReadOnlyList<FileEntry> List(string path)
    {
        var full = Resolve(path);
        if (!Directory.Exists(full))
        {
            throw new FileSystemException($"{path} is not a directory", FileSystemCodes.NotFound);
        }

        var entries = new List<FileEntry>();
        foreach (var child in Directory.EnumerateFileSystemEntries(full))
        {
            var stat = Stat(child);
            if (stat is not null) entries.Add(stat);
        }

        entries.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        return entries;
    }

    /// <inheritdoc />
    public override (IReadOnlyList<FileEntry> Files, bool Truncated) Glob(
        string root,
        string pattern,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var full = Resolve(root);
        if (!Directory.Exists(full)) return ([], false);

        var matcher = new GlobMatcher(pattern);
        var matches = new List<FileEntry>();
        var truncated = false;

        foreach (var file in EnumerateFiles(full, cancellationToken))
        {
            var relative = Path.GetRelativePath(full, file).Replace('\\', '/');
            if (!matcher.Matches(relative)) continue;

            var info = new FileInfo(file);
            matches.Add(new FileEntry(file, false, info.Length, info.LastWriteTimeUtc));
            if (matches.Count > limit * 4)
            {
                truncated = true;
                break;
            }
        }

        matches.Sort(static (left, right) => right.ModifiedAt.CompareTo(left.ModifiedAt));
        if (matches.Count > limit)
        {
            truncated = true;
            matches = matches.GetRange(0, limit);
        }

        return (matches, truncated);
    }

    private static IEnumerable<string> EnumerateFiles(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (UnauthorizedAccessException)
            {
                // A directory the user cannot read is not an error for a search; it is
                // simply not part of the results.
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var child in children)
            {
                if (Directory.Exists(child))
                {
                    var name = Path.GetFileName(child);
                    if (name is ".git" or "node_modules" or "bin" or "obj") continue;
                    pending.Push(child);
                }
                else
                {
                    yield return child;
                }
            }
        }
    }

    /// <summary>
    /// Whether bytes look like something other than text.
    /// </summary>
    /// <remarks>
    /// A NUL byte in the first few kilobytes is the practical signal. Handing a model
    /// a decoded binary is worse than refusing: it wastes context and tells it nothing.
    /// </remarks>
    private static bool LooksBinary(byte[] bytes)
    {
        var limit = Math.Min(bytes.Length, 8000);
        for (var index = 0; index < limit; index++)
        {
            if (bytes[index] == 0) return true;
        }

        return false;
    }

    /// <summary>Mount the local filesystem provider.</summary>
    /// <param name="workspace">The directory relative paths resolve against.</param>
    /// <returns>The plugin to hand to <see cref="Context.Plugin" />.</returns>
    public static IPlugin Plugin(string workspace)
        => ServicePlugin.Create<FileSystemService>(
            "fs-local",
            FsKeys.Service,
            ctx => new LocalFileSystem(ctx, workspace));
}

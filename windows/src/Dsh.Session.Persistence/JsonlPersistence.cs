using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dsh.Cordis;
using Dsh.Llm;
using Dsh.Util;

namespace Dsh.Session.Persistence;

/// <summary>The first line of a session log: the metadata, tagged so it cannot be read as an event.</summary>
/// <param name="Type">Always <c>session</c>.</param>
/// <param name="Version">The format version this log was written with.</param>
/// <param name="Id">The session's id.</param>
/// <param name="CreatedAt">When the session was created, in Unix epoch milliseconds.</param>
/// <param name="Cwd">The workspace it belongs to.</param>
/// <param name="ParentSession">The session it was forked from.</param>
/// <param name="SeedLength">How many leading events were inherited.</param>
/// <param name="Origin">Coarse classification, such as a subagent child.</param>
/// <param name="DelegationDepth">Zero for a top-level session.</param>
/// <param name="AgentPreset">The preset the agent was composed from.</param>
public sealed record SessionHeaderLine(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("createdAt")] long CreatedAt,
    [property: JsonPropertyName("cwd")] string? Cwd,
    [property: JsonPropertyName("parentSession")] string? ParentSession,
    [property: JsonPropertyName("seedLength")] int? SeedLength,
    [property: JsonPropertyName("origin")] string? Origin,
    [property: JsonPropertyName("delegationDepth")] int DelegationDepth,
    [property: JsonPropertyName("agentPreset")] string? AgentPreset);

/// <summary>A stored log this build cannot read.</summary>
public sealed class SessionFormatException : HarnessError
{
    /// <param name="message">What to tell the user, including what would fix it.</param>
    public SessionFormatException(string message) : base(message, "SESSION_FORMAT_UNSUPPORTED") { }
}

/// <summary>A stored log that is damaged rather than merely unfinished.</summary>
public sealed class SessionCorruptException : HarnessError
{
    /// <param name="message">What was wrong with the file.</param>
    public SessionCorruptException(string message) : base(message, "SESSION_CORRUPT") { }
}

/// <summary>What a session listing shows without opening the whole log.</summary>
/// <param name="Header">The session's metadata.</param>
/// <param name="Path">Where its log lives.</param>
/// <param name="UpdatedAt">When the log was last written.</param>
public sealed record StoredSession(SessionHeader Header, string Path, DateTimeOffset UpdatedAt);

/// <summary>
/// Keeps session logs on disk as JSON lines.
/// </summary>
/// <remarks>
/// Creation is lazy: nothing is written until a session actually records something,
/// so an abandoned session leaves nothing behind. Appends are write-behind and
/// flushed at the durability barrier, which keeps a streaming turn from paying a
/// synchronous write per token.
///
/// Logs the Node harness compressed are readable here too, so the two can share a
/// store. This build writes plain lines, which that harness also reads.
/// </remarks>
public sealed class JsonlPersistence : Cordis.Service
{
    private readonly string _root;
    private readonly Dictionary<SessionId, string> _paths = [];
    private readonly Dictionary<SessionId, List<SessionEvent>> _pending = [];
    private readonly object _gate = new();

    /// <param name="ctx">The mounting plugin's context.</param>
    /// <param name="root">The store's root; <c>sessions</c> in the harness home when omitted.</param>
    public JsonlPersistence(Context ctx, string? root = null) : base(ctx, PersistenceKeys.Service)
    {
        _root = root ?? HomePaths.Combine("sessions");
        SessionEvents.EnsureRegistered();
    }

    /// <summary>The store's root directory.</summary>
    public string Root => _root;

    /// <summary>
    /// Install the write path on a session store.
    /// </summary>
    /// <param name="ctx">The registering context.</param>
    /// <returns>A disposer removing the listeners.</returns>
    public IDisposable Install(Context ctx)
    {
        var created = ctx.On(SessionKeys.Created, session =>
        {
            lock (_gate) _paths[session.Id] = SessionPaths.LogPath(_root, session.Header.Cwd, session.Id);
        });

        var appended = ctx.On(SessionKeys.Event, notice =>
        {
            lock (_gate)
            {
                if (!_pending.TryGetValue(notice.Session.Id, out var queue))
                {
                    queue = [];
                    _pending[notice.Session.Id] = queue;
                }

                queue.Add(notice.Event);
            }

            // The end of a turn is the durability checkpoint: a completed turn is what a
            // person would expect to survive a crash, and flushing per event would cost
            // a synchronous write per streamed token.
            if (string.Equals(notice.Event.Type, SessionEvents.TurnEnd.Name, StringComparison.Ordinal))
            {
                Flush(notice.Session);
            }
        });

        var flushed = ctx.OnParallel(SessionKeys.Flush, session =>
        {
            Flush(session);
            return Task.CompletedTask;
        });

        var disposed = ctx.On(SessionKeys.Disposed, Flush);

        return new ActionDisposable(() =>
        {
            created.Dispose();
            appended.Dispose();
            flushed.Dispose();
            disposed.Dispose();
        });
    }

    /// <summary>
    /// Write out everything queued for one session.
    /// </summary>
    /// <param name="session">The session to flush.</param>
    public void Flush(Session session)
    {
        List<SessionEvent> queued;
        string path;

        lock (_gate)
        {
            if (!_pending.TryGetValue(session.Id, out var pending) || pending.Count == 0) return;
            queued = pending;
            _pending[session.Id] = [];
            path = _paths.TryGetValue(session.Id, out var known)
                ? known
                : SessionPaths.LogPath(_root, session.Header.Cwd, session.Id);
            _paths[session.Id] = path;
        }

        var lines = new List<string>(queued.Count + 1);
        if (!File.Exists(path)) lines.Add(JsonSerializer.Serialize(HeaderLineOf(session.Header), SessionJson.Options));
        foreach (var entry in queued) lines.Add(SessionJson.Line(entry));

        AtomicFile.AppendLines(path, lines);
    }

    /// <summary>
    /// List every stored session, newest first.
    /// </summary>
    /// <returns>
    /// One entry per session, read from each log's first line only — so a picker's
    /// cost scales with how many sessions there are, not how long they ran.
    /// </returns>
    public IReadOnlyList<StoredSession> List()
    {
        if (!Directory.Exists(_root)) return [];

        var sessions = new List<StoredSession>();
        foreach (var project in Directory.EnumerateDirectories(_root))
        {
            foreach (var directory in Directory.EnumerateDirectories(project))
            {
                var path = LogPathIn(directory);
                if (path is null) continue;

                try
                {
                    var header = ReadHeader(path);
                    sessions.Add(new StoredSession(header, path, File.GetLastWriteTimeUtc(path)));
                }
                catch (Exception error) when (error is SessionFormatException or SessionCorruptException or IOException)
                {
                    // One unreadable session must not hide every other from the list.
                    Ctx.Logger.Log(LogLevel.Warn, "session-persistence", $"skipping {path}", error);
                }
            }
        }

        sessions.Sort(static (left, right) => right.UpdatedAt.CompareTo(left.UpdatedAt));
        return sessions;
    }

    /// <summary>
    /// Read one stored session's whole log.
    /// </summary>
    /// <param name="path">The log's path.</param>
    /// <returns>Its metadata and every event, in order.</returns>
    /// <exception cref="SessionFormatException">It was written by an incompatible build.</exception>
    /// <exception cref="SessionCorruptException">Its committed region is damaged.</exception>
    public static (SessionHeader Header, IReadOnlyList<SessionEvent> Events) Read(string path)
    {
        var lines = ReadLines(path);
        if (lines.Count == 0) throw new SessionCorruptException($"{path} is empty");

        var header = ParseHeader(lines[0], path);
        var events = new List<SessionEvent>();

        for (var index = 1; index < lines.Count; index++)
        {
            if (lines[index].Length == 0) continue;

            SessionEvent entry;
            try
            {
                entry = SessionJson.Parse(lines[index]);
            }
            catch (JsonException error) when (index == lines.Count - 1 && error is not UnknownSessionEventException)
            {
                // A half-written final line is an interrupted run, not damage: the
                // committed events before it are all real. An unrecognized event is a
                // different matter and is never swallowed by this tolerance.
                break;
            }

            if (entry.Seq != events.Count)
            {
                throw new SessionCorruptException(
                    $"{path}: sequence gap at line {index + 1} (expected {events.Count}, got {entry.Seq})");
            }

            events.Add(entry);
        }

        return (header, events);
    }

    /// <summary>
    /// Reopen a stored session, closing any turn a crash left open.
    /// </summary>
    /// <param name="path">The log's path.</param>
    /// <returns>The session, ready to continue.</returns>
    public static Session Resume(string path)
    {
        var (header, events) = Read(path);
        var repaired = new List<SessionEvent>(events);

        foreach (var closer in SessionRepair.InterruptedTurnClosers(events))
        {
            // The repair reuses the last real event's time rather than inventing a
            // later one: nothing happened after the crash.
            repaired.Add(new SessionEvent
            {
                Type = closer.Type,
                Seq = repaired.Count,
                Time = repaired.Count > 0 ? repaired[^1].Time : header.CreatedAt,
                Data = closer.Data,
                SurfaceOp = closer.Intent?.Op,
                SourceEventSeqs = closer.Intent?.SourceEventSeqs,
            });
        }

        return new Session(header with { SeedLength = repaired.Count }, repaired);
    }

    private static SessionHeader ParseHeader(string line, string path)
    {
        SessionHeaderLine? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<SessionHeaderLine>(line, SessionJson.Options);
        }
        catch (JsonException error)
        {
            throw new SessionCorruptException($"{path}: the header line is not valid JSON: {error.Message}");
        }

        if (parsed is null || parsed.Type != "session")
        {
            throw new SessionCorruptException($"{path}: the first line is not a session header");
        }

        // Checked before anything else is interpreted, so a newer log reports "upgrade
        // the harness" rather than a confusing shape error deeper in the file.
        if (parsed.Version != Session.FormatVersion)
        {
            throw new SessionFormatException(parsed.Version > Session.FormatVersion
                ? $"session \"{parsed.Id}\" uses log format v{parsed.Version}, but this build reads only v{Session.FormatVersion}: upgrade the harness to open it"
                : $"session \"{parsed.Id}\" uses log format v{parsed.Version}, older than the supported v{Session.FormatVersion}, and this build ships no upgrade path for it");
        }

        return new SessionHeader(
            parsed.Version,
            new SessionId(parsed.Id),
            parsed.CreatedAt,
            parsed.Cwd,
            parsed.ParentSession is null ? null : new SessionId(parsed.ParentSession),
            parsed.SeedLength,
            parsed.Origin,
            parsed.DelegationDepth,
            parsed.AgentPreset);
    }

    private static SessionHeaderLine HeaderLineOf(SessionHeader header) => new(
        "session",
        header.Version,
        header.Id.Value,
        header.CreatedAt,
        header.Cwd,
        header.ParentSession?.Value,
        header.SeedLength,
        header.Origin,
        header.DelegationDepth,
        header.AgentPreset);

    private static SessionHeader ReadHeader(string path)
    {
        using var stream = OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var first = reader.ReadLine() ?? throw new SessionCorruptException($"{path} is empty");
        return ParseHeader(first, path);
    }

    private static string? LogPathIn(string directory)
    {
        var plain = Path.Combine(directory, SessionPaths.LogFileName);
        if (File.Exists(plain)) return plain;

        var compressed = Path.Combine(directory, SessionPaths.CompressedLogFileName);
        return File.Exists(compressed) ? compressed : null;
    }

    private static List<string> ReadLines(string path)
    {
        using var stream = OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    /// <summary>
    /// Open a log, decompressing when the Node harness wrote it compressed.
    /// </summary>
    private static Stream OpenRead(string path)
    {
        var file = File.OpenRead(path);
        if (!path.EndsWith(".zstd", StringComparison.Ordinal)) return file;

        try
        {
            return new ZstdSharp.DecompressionStream(file);
        }
        catch (Exception)
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Mount JSONL persistence and install its write path.</summary>
    /// <param name="root">The store's root; <c>sessions</c> in the harness home when omitted.</param>
    /// <returns>The plugin to hand to <see cref="Context.Plugin" />.</returns>
    public static IPlugin Plugin(string? root = null)
        => new FunctionPlugin(
            "session-persistence-jsonl",
            ctx =>
            {
                var persistence = new JsonlPersistence(ctx, root);
                ctx.Provide(PersistenceKeys.Service, persistence);
                persistence.Install(ctx);
                return Task.CompletedTask;
            },
            SessionKeys.Service);
}

/// <summary>The context key session persistence is published under.</summary>
public static class PersistenceKeys
{
    /// <summary>The context key a persistence backend claims.</summary>
    public const string Service = "sessionPersistence";
}

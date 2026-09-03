using Dsh.Cordis;
using Dsh.Llm;

namespace Dsh.Session;

/// <summary>Context and event keys the session capability publishes.</summary>
public static class SessionKeys
{
    /// <summary>The context key <see cref="SessionStore" /> is published under.</summary>
    public const string Service = "sessions";

    /// <summary>
    /// A session has been published. A listener that throws here vetoes publication,
    /// which is the one place a notification is allowed to.
    /// </summary>
    public static EmitKey<Session> Created { get; } = new("session/created");

    /// <summary>A session has been detached from the store.</summary>
    public static EmitKey<Session> Disposed { get; } = new("session/disposed");

    /// <summary>One event was appended to a published session's log.</summary>
    public static EmitKey<SessionEventNotice> Event { get; } = new("session/event");

    /// <summary>
    /// A durability barrier: every listener must have committed the session's events
    /// before this completes.
    /// </summary>
    public static ParallelKey<Session> Flush { get; } = new("session/flush");
}

/// <summary>One appended event, with the session it belongs to.</summary>
/// <param name="Session">The session whose log grew.</param>
/// <param name="Event">The appended event.</param>
public sealed record SessionEventNotice(Session Session, SessionEvent Event);

/// <summary>
/// A session constructed but not yet published, so a caller that owns an ordered
/// teardown can install its own rollback before anything can observe the session.
/// </summary>
/// <param name="Session">The constructed session.</param>
public sealed record SessionPreparation(Session Session);

/// <summary>Why a fork was refused.</summary>
public sealed class SessionForkException : HarnessError
{
    /// <param name="message">What was wrong.</param>
    /// <param name="code">The machine-readable reason.</param>
    public SessionForkException(string message, string code) : base(message, code) { }
}

/// <summary>
/// The live registry of open sessions.
/// </summary>
/// <remarks>
/// Publication is split into prepare, enter, and announce so a caller can hold the
/// detach handle before the creation notice fires: a listener that vetoes creation
/// then rolls the attach back instead of leaving a half-registered session behind.
/// </remarks>
public sealed class SessionStore : Service
{
    private sealed class Entry
    {
        public required Session Session { get; init; }
        public IDisposable? Subscription { get; set; }
        public bool Announced { get; set; }
    }

    private readonly Dictionary<SessionId, Entry> _entries = [];
    private readonly object _gate = new();

    /// <param name="ctx">The mounting plugin's context.</param>
    public SessionStore(Context ctx) : base(ctx, SessionKeys.Service) => SessionEvents.EnsureRegistered();

    /// <summary>
    /// Mint an id no other session is using.
    /// </summary>
    /// <returns>A fresh session id.</returns>
    /// <remarks>
    /// Random rather than sequential, because the id names a directory in a store that
    /// outlives the process. A counter restarts at one in every run, so a second run
    /// against the same store would remint the first run's id and append into its log
    /// — losing both sessions into one unreadable file. Uniqueness has to hold across
    /// processes, and only the id itself can carry that.
    /// </remarks>
    public SessionId MintId()
    {
        lock (_gate)
        {
            while (true)
            {
                var candidate = new SessionId($"session-{Guid.NewGuid():N}"[..20]);
                if (!_entries.ContainsKey(candidate)) return candidate;
            }
        }
    }

    /// <summary>
    /// Construct a session without publishing it.
    /// </summary>
    /// <param name="header">The session's storage metadata.</param>
    /// <param name="seed">History inherited from a resume, fork, or replay.</param>
    /// <returns>The unpublished session.</returns>
    public SessionPreparation Prepare(SessionHeader header, IReadOnlyList<SessionEvent>? seed = null)
        => new(new Session(header, seed) { Logger = Ctx.Logger });

    /// <summary>
    /// Register a prepared session so it can be found by id and its events broadcast.
    /// </summary>
    /// <param name="session">The session to register.</param>
    /// <returns>A disposer that detaches it and announces the disposal.</returns>
    /// <exception cref="InvalidOperationException">The id is already registered.</exception>
    public IDisposable Enter(Session session)
    {
        Entry entry;
        lock (_gate)
        {
            if (_entries.ContainsKey(session.Id))
            {
                throw new InvalidOperationException($"session \"{session.Id}\" is already registered");
            }

            entry = new Entry { Session = session };
            _entries[session.Id] = entry;
        }

        entry.Subscription = session.OnEvent(logged =>
            Ctx.Emit(SessionKeys.Event, new SessionEventNotice(session, logged)));

        return new ActionDisposable(() =>
        {
            bool announced;
            lock (_gate)
            {
                if (!_entries.TryGetValue(session.Id, out var current) || !ReferenceEquals(current, entry)) return;
                _entries.Remove(session.Id);
                announced = entry.Announced;
            }

            entry.Subscription?.Dispose();

            // A session whose creation edge was never announced gets no disposal edge:
            // a rolled-back registration must not invent a lifecycle it never had.
            if (announced) Ctx.Emit(SessionKeys.Disposed, session);
        });
    }

    /// <summary>
    /// Announce a registered session's creation, exactly once.
    /// </summary>
    /// <param name="session">The registered session.</param>
    /// <exception cref="Exception">A listener's veto, which the caller turns into a rollback.</exception>
    public void Announce(Session session)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(session.Id, out var entry) || entry.Announced) return;
            entry.Announced = true;
        }

        Ctx.Emit(SessionKeys.Created, session);
    }

    /// <summary>
    /// Construct, register, and announce a session in one step.
    /// </summary>
    /// <param name="header">The session's storage metadata.</param>
    /// <param name="seed">History inherited from a resume, fork, or replay.</param>
    /// <returns>The published session and the disposer that detaches it.</returns>
    public (Session Session, IDisposable Detach) Create(
        SessionHeader header,
        IReadOnlyList<SessionEvent>? seed = null)
    {
        var prepared = Prepare(header, seed);
        var detach = Enter(prepared.Session);
        try
        {
            Announce(prepared.Session);
        }
        catch
        {
            detach.Dispose();
            throw;
        }

        return (prepared.Session, detach);
    }

    /// <summary>
    /// Find a registered session.
    /// </summary>
    /// <param name="id">The session id.</param>
    /// <returns>The session, or null when it is not open.</returns>
    public Session? Get(SessionId id)
    {
        lock (_gate) return _entries.GetValueOrDefault(id)?.Session;
    }

    /// <summary>Every open session.</summary>
    public IReadOnlyList<Session> Sessions
    {
        get
        {
            lock (_gate) return [.. _entries.Values.Select(static entry => entry.Session)];
        }
    }

    /// <summary>
    /// Branch a new session from a prefix of an open one.
    /// </summary>
    /// <param name="source">The session to branch from.</param>
    /// <param name="boundary">
    /// How many leading events the child inherits; the whole log when omitted.
    /// </param>
    /// <param name="childId">The child's id; minted when omitted.</param>
    /// <returns>The forked session and the disposer that detaches it.</returns>
    /// <exception cref="SessionForkException">
    /// The boundary is out of range, or it would cut inside an open turn — a child
    /// starting mid-turn would inherit a conversation the model cannot answer.
    /// </exception>
    public (Session Session, IDisposable Detach) Fork(
        Session source,
        int? boundary = null,
        SessionId? childId = null)
    {
        var events = source.Events;
        var cut = boundary ?? events.Count;
        if (cut < 0 || cut > events.Count)
        {
            throw new SessionForkException(
                $"fork boundary {cut} is outside session \"{source.Id}\" (0..{events.Count})",
                "INVALID_BOUNDARY");
        }

        var openTurn = false;
        for (var index = 0; index < cut; index++)
        {
            var type = events[index].Type;
            if (string.Equals(type, SessionEvents.TurnStart.Name, StringComparison.Ordinal)) openTurn = true;
            else if (string.Equals(type, SessionEvents.TurnEnd.Name, StringComparison.Ordinal)) openTurn = false;
        }

        if (openTurn)
        {
            throw new SessionForkException(
                $"fork boundary {cut} falls inside an open turn of session \"{source.Id}\"",
                "OPEN_TURN");
        }

        var id = childId ?? MintId();
        lock (_gate)
        {
            if (_entries.ContainsKey(id))
            {
                throw new SessionForkException($"session \"{id}\" already exists", "SESSION_ALREADY_EXISTS");
            }
        }

        var seed = events.Take(cut).ToArray();
        var header = source.Header with
        {
            Id = id,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ParentSession = source.Id,
            SeedLength = seed.Length,
        };

        return Create(header, seed);
    }

    /// <summary>
    /// Wait until every persistence listener has committed a session's events.
    /// </summary>
    /// <param name="session">The session to flush.</param>
    /// <returns>A task completing once the barrier has passed.</returns>
    public Task FlushAsync(Session session) => Ctx.ParallelAsync(SessionKeys.Flush, session);

    /// <summary>
    /// Build a header for a brand-new session.
    /// </summary>
    /// <param name="id">The session id.</param>
    /// <param name="cwd">The workspace the session belongs to.</param>
    /// <param name="agentPreset">The preset the agent was composed from.</param>
    /// <returns>A header stamped with this build's format version.</returns>
    public static SessionHeader NewHeader(SessionId id, string? cwd = null, string? agentPreset = null)
        => new(
            Session.FormatVersion,
            id,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            cwd,
            AgentPreset: agentPreset);

    /// <summary>Mount the session capability.</summary>
    /// <returns>The plugin to hand to <see cref="Context.Plugin" />.</returns>
    public static IPlugin Plugin()
        => ServicePlugin.Create("session", SessionKeys.Service, ctx => new SessionStore(ctx));
}

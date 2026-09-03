using Dsh.Cordis;
using Dsh.Llm;

namespace Dsh.Session;

/// <summary>
/// Immutable storage metadata, kept outside the conversation log.
/// </summary>
/// <param name="Version">The on-disk format version this log was written with.</param>
/// <param name="Id">The session's id.</param>
/// <param name="CreatedAt">Unix epoch milliseconds when the session was created.</param>
/// <param name="Cwd">The absolute working directory the session belongs to.</param>
/// <param name="ParentSession">The session this one was forked from.</param>
/// <param name="SeedLength">How many leading events were inherited through a seed.</param>
/// <param name="Origin">Coarse classification, such as a subagent child.</param>
/// <param name="DelegationDepth">Zero for a top-level session, parent depth plus one for a child.</param>
/// <param name="AgentPreset">The preset the session's agent was composed from.</param>
public sealed record SessionHeader(
    int Version,
    SessionId Id,
    long CreatedAt,
    string? Cwd = null,
    SessionId? ParentSession = null,
    int? SeedLength = null,
    string? Origin = null,
    int DelegationDepth = 0,
    string? AgentPreset = null);

/// <summary>
/// The append-only log of one agent interaction, and the only source of the context
/// a model sees.
/// </summary>
/// <remarks>
/// Two properties hold by construction and everything else depends on them: an
/// event's <see cref="SessionEvent.Seq" /> equals its index in the log, and a message
/// reaches the model only by being placed on the surface. Together they mean a
/// request can be rebuilt exactly from the log — which is what makes replay, fork,
/// resume, and transcripts all agree with what actually happened.
/// </remarks>
public sealed class Session
{
    /// <summary>
    /// The on-disk format version this build writes and accepts.
    /// </summary>
    /// <remarks>
    /// Pinned while the harness is unreleased: no compatibility is implied, an
    /// incompatible log is refused, and no migration is provided. Bump it only when
    /// an older runtime could no longer read a new log with full semantic
    /// correctness — a changed envelope, header, or surface mechanism. Adding an
    /// event type does not qualify; <see cref="SessionEvent.Ignorable" /> covers
    /// vocabulary growth.
    /// </remarks>
    public const int FormatVersion = 0;

    private readonly List<SessionEvent> _log = [];
    private readonly SurfaceManager _surface = new();
    private readonly List<Action<SessionEvent>> _listeners = [];
    private readonly object _gate = new();

    private IReadOnlyList<SessionEvent>? _snapshot;
    private List<Message> _derived = [];
    private int _derivedNodes;
    private int _derivedGeneration;
    private EpochHeader? _foldedHeader;
    private int _headerFoldSeq;
    private RequestContextData? _foldedContext;
    private int _contextFoldSeq;
    private bool _appending;

    /// <summary>
    /// Open a session, optionally over inherited history.
    /// </summary>
    /// <param name="header">The session's storage metadata.</param>
    /// <param name="seed">Events inherited from a resume, fork, or replay.</param>
    /// <exception cref="InvalidOperationException">A seed event is malformed or out of order.</exception>
    public Session(SessionHeader header, IReadOnlyList<SessionEvent>? seed = null)
    {
        SessionEvents.EnsureRegistered();
        Header = header;

        if (seed is { Count: > 0 })
        {
            for (var index = 0; index < seed.Count; index++)
            {
                var entry = seed[index];
                if (entry.Seq != index)
                {
                    throw new InvalidOperationException(
                        $"session \"{header.Id}\" seed is not contiguous: event at index {index} claims seq {entry.Seq}");
                }

                if (!SessionEventRegistry.IsKnown(entry.Type) && !entry.Ignorable)
                {
                    throw new InvalidOperationException(
                        $"session \"{header.Id}\" contains unrecognized required event \"{entry.Type}\"; refusing to reconstruct it");
                }

                var plan = _surface.Plan(entry);
                _log.Add(entry);
                _surface.Apply(plan);
            }
        }

        FirstLiveSeq = _log.Count;

        // The boundary is durable so a later reader can tell inherited history from
        // this lifecycle's own work. A seed that already ends in one is not re-marked,
        // so reopening an untouched session does not grow its log.
        if (seed is { Count: > 0 }
            && !string.Equals(_log[^1].Type, SessionEvents.EndSeed.Name, StringComparison.Ordinal))
        {
            Append(SessionEvents.EndSeed, SessionEndSeedData.Instance);
        }
    }

    /// <summary>The session's storage metadata.</summary>
    public SessionHeader Header { get; }

    /// <summary>The session's id.</summary>
    public SessionId Id => Header.Id;

    /// <summary>The first seq this lifecycle produced; everything before it came from a seed.</summary>
    public int FirstLiveSeq { get; }

    /// <summary>How many events the log holds, which is also the next seq.</summary>
    public int Seq
    {
        get
        {
            lock (_gate) return _log.Count;
        }
    }

    /// <summary>
    /// The log as an immutable snapshot. A snapshot handed out earlier never grows,
    /// so a reader iterating one cannot see a concurrent append halfway through.
    /// </summary>
    public IReadOnlyList<SessionEvent> Events
    {
        get
        {
            lock (_gate) return _snapshot ??= _log.ToArray();
        }
    }

    /// <summary>The current model-visible surface.</summary>
    public SurfaceState Surface => _surface.State;

    /// <summary>
    /// Append one event to the log.
    /// </summary>
    /// <typeparam name="TData">The payload type registered for this event name.</typeparam>
    /// <param name="type">The vocabulary entry being appended.</param>
    /// <param name="data">The payload.</param>
    /// <param name="intent">
    /// Where the event lands on the surface. Required for the message-producing types
    /// and rejected for every other.
    /// </param>
    /// <param name="ignorable">Whether a reader that does not know this type may skip it.</param>
    /// <returns>The logged event, carrying the seq and time it was assigned.</returns>
    /// <exception cref="InvalidOperationException">The placement is invalid, or an append re-entered another.</exception>
    public SessionEvent Append<TData>(
        SessionEventType<TData> type,
        TData data,
        SurfaceIntent? intent = null,
        bool ignorable = false)
        where TData : notnull
    {
        SessionEvent entry;
        Action<SessionEvent>[] listeners;

        lock (_gate)
        {
            if (_appending)
            {
                throw new InvalidOperationException(
                    "session append cannot re-enter while another append is being published");
            }

            entry = new SessionEvent
            {
                Type = type.Name,
                Seq = _log.Count,
                Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Data = data,
                Ignorable = ignorable,
                SurfaceOp = intent?.Op,
                SourceEventSeqs = intent?.SourceEventSeqs,
            };

            // Planned before the log is touched: a rejected placement must leave both
            // the log and the surface exactly as they were.
            var plan = _surface.Plan(entry);

            // Resolved before publication so a listener registered during dispatch does
            // not observe the event that registered it.
            listeners = [.. _listeners];

            _log.Add(entry);
            _surface.Apply(plan);
            _snapshot = null;
            _appending = true;
        }

        try
        {
            foreach (var listener in listeners)
            {
                try
                {
                    listener(entry);
                }
                catch (Exception error)
                {
                    Logger?.Log(LogLevel.Warn, "session/event", $"listener failed on \"{entry.Type}\"", error);
                }
            }
        }
        finally
        {
            lock (_gate) _appending = false;
        }

        return entry;
    }

    /// <summary>Where contained listener failures are reported.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>
    /// Observe every event appended after registration.
    /// </summary>
    /// <param name="listener">Called once per append, after the event is committed.</param>
    /// <returns>A disposer removing the listener.</returns>
    public IDisposable OnEvent(Action<SessionEvent> listener)
    {
        lock (_gate) _listeners.Add(listener);
        return new ActionDisposable(() =>
        {
            lock (_gate) _listeners.Remove(listener);
        });
    }

    /// <summary>
    /// The model history this log projects.
    /// </summary>
    /// <returns>
    /// The messages a request carries, in surface order. Cached incrementally: new
    /// surface nodes are projected as they arrive, and a positional replacement
    /// invalidates the cache so the shadowed history really disappears.
    /// </returns>
    public IReadOnlyList<Message> DeriveMessages()
    {
        lock (_gate)
        {
            var nodes = _surface.State.Nodes;
            var generation = _surface.State.ReplaceGeneration;

            if (generation != _derivedGeneration)
            {
                _derived = [];
                _derivedNodes = 0;
                _derivedGeneration = generation;
            }

            for (var index = _derivedNodes; index < nodes.Count; index++)
            {
                var message = SurfaceProjection.Project(_log[nodes[index]]);
                if (message is not null) _derived.Add(message);
            }

            _derivedNodes = nodes.Count;
            return _derived.ToArray();
        }
    }

    /// <summary>
    /// The latest request header recorded in the log.
    /// </summary>
    /// <returns>The header, or null when no request has been made yet.</returns>
    public EpochHeader? RequestHeader()
    {
        lock (_gate)
        {
            for (; _headerFoldSeq < _log.Count; _headerFoldSeq++)
            {
                var entry = _log[_headerFoldSeq];
                if (string.Equals(entry.Type, SessionEvents.RequestHeader.Name, StringComparison.Ordinal))
                {
                    _foldedHeader = entry.DataAs<RequestHeaderData>().Header;
                }
            }

            return _foldedHeader;
        }
    }

    /// <summary>
    /// The latest route metadata recorded in the log.
    /// </summary>
    /// <returns>The route metadata, or null when none was recorded.</returns>
    public RequestContextData? RequestContext()
    {
        lock (_gate)
        {
            for (; _contextFoldSeq < _log.Count; _contextFoldSeq++)
            {
                var entry = _log[_contextFoldSeq];
                if (string.Equals(entry.Type, SessionEvents.RequestContext.Name, StringComparison.Ordinal))
                {
                    _foldedContext = entry.DataAs<RequestContextData>();
                }
            }

            return _foldedContext;
        }
    }

    /// <summary>
    /// The highest turn number the log opened.
    /// </summary>
    /// <returns>The last turn number, or zero when no turn has started.</returns>
    /// <remarks>A resumed agent continues numbering from here rather than restarting at one.</remarks>
    public int LastTurn()
    {
        var events = Events;
        for (var index = events.Count - 1; index >= 0; index--)
        {
            if (string.Equals(events[index].Type, SessionEvents.TurnStart.Name, StringComparison.Ordinal))
            {
                return events[index].DataAs<TurnStartData>().Turn;
            }
        }

        return 0;
    }

    /// <summary>
    /// The checklist as of the latest write.
    /// </summary>
    /// <returns>The current list, or null when nothing has been written.</returns>
    public IReadOnlyList<TodoItem>? Todos()
    {
        var events = Events;
        for (var index = events.Count - 1; index >= 0; index--)
        {
            if (string.Equals(events[index].Type, SessionEvents.TodoWrite.Name, StringComparison.Ordinal))
            {
                return events[index].DataAs<TodoWriteData>().Todos;
            }
        }

        return null;
    }
}

namespace Dsh.Session;

/// <summary>
/// How an event entered the ordered surface — the model-visible projection of the
/// log.
/// </summary>
public abstract record SurfaceOp;

/// <summary>Added to the end of the surface: the ordinary path for a new message.</summary>
public sealed record AppendOp : SurfaceOp
{
    /// <summary>The shared instance; the operation carries no state.</summary>
    public static AppendOp Instance { get; } = new();
}

/// <summary>
/// Replaces a contiguous run of surface nodes with this one. Compaction uses it to
/// shadow older history behind a summary without deleting anything from the log.
/// </summary>
/// <param name="Start">Seq of the first shadowed surface node, inclusive.</param>
/// <param name="End">Seq of the last shadowed surface node, inclusive.</param>
public sealed record ReplaceOp(int Start, int End) : SurfaceOp;

/// <summary>
/// Where an appended event lands on the surface, and which earlier events it cites.
/// Required on the three message-producing event types and forbidden on every other,
/// so an event's model visibility is always declared rather than inferred.
/// </summary>
/// <param name="Op">The placement.</param>
/// <param name="SourceEventSeqs">
/// Seqs of the earlier events this one was built from — the streamed chunks behind
/// an assistant message, or the surface nodes a replacement shadows.
/// </param>
public sealed record SurfaceIntent(SurfaceOp Op, IReadOnlyList<int>? SourceEventSeqs = null);

/// <summary>
/// One immutable entry in the session log.
/// </summary>
/// <remarks>
/// <see cref="Seq" /> is always the entry's index in the log, which is what lets a
/// citation be a plain integer and a reader detect a gap without a scan.
/// </remarks>
public sealed record SessionEvent
{
    /// <summary>The event's vocabulary name, such as <c>turn/start</c>.</summary>
    public required string Type { get; init; }

    /// <summary>Position in the log; always equal to the entry's index.</summary>
    public required int Seq { get; init; }

    /// <summary>When it was appended, in Unix epoch milliseconds.</summary>
    public required long Time { get; init; }

    /// <summary>The typed payload for this event's vocabulary entry.</summary>
    public required object Data { get; init; }

    /// <summary>
    /// Whether a reader that does not recognize <see cref="Type" /> may skip this
    /// event. False — the default — means a reader meeting an unknown type must
    /// refuse the session rather than silently reconstruct it without this event.
    /// </summary>
    public bool Ignorable { get; init; }

    /// <summary>How this event entered the surface; absent for a log-only event.</summary>
    public SurfaceOp? SurfaceOp { get; init; }

    /// <summary>Seqs of the earlier events this one cites as its sources.</summary>
    public IReadOnlyList<int>? SourceEventSeqs { get; init; }

    /// <summary>
    /// Read the payload as its declared type.
    /// </summary>
    /// <typeparam name="TData">The payload type registered for this event name.</typeparam>
    /// <returns>The payload.</returns>
    /// <exception cref="InvalidCastException">The event carries another payload type.</exception>
    public TData DataAs<TData>() => (TData)Data;

    /// <summary>
    /// Read the payload when it might be another type.
    /// </summary>
    /// <typeparam name="TData">The payload type to try.</typeparam>
    /// <returns>The payload, or null when the event carries something else.</returns>
    public TData? DataOrNull<TData>() where TData : class => Data as TData;
}

/// <summary>
/// One entry of the durable event vocabulary: a name bound to the payload type it
/// carries.
/// </summary>
/// <typeparam name="TData">The payload type.</typeparam>
/// <param name="Name">The event name written to disk.</param>
public sealed record SessionEventType<TData>(string Name) where TData : notnull;

/// <summary>
/// The durable event vocabulary. Plugins extend it by registering their own event
/// names, which is how the log grows without the core knowing every producer.
/// </summary>
/// <remarks>
/// A name must be registered before it can be appended or read back, so a log
/// containing an unrecognized required event is refused rather than reconstructed
/// with a hole in it.
/// </remarks>
public static class SessionEventRegistry
{
    private sealed record Entry(Type DataType, bool SurfaceEligible);

    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>
    /// Add one event name to the vocabulary.
    /// </summary>
    /// <typeparam name="TData">The payload type the name carries.</typeparam>
    /// <param name="name">The event name.</param>
    /// <param name="surfaceEligible">
    /// Whether events of this type may appear on the model-visible surface. Only the
    /// three message-producing types are.
    /// </param>
    /// <returns>The typed handle used to append and read events of this type.</returns>
    /// <exception cref="InvalidOperationException">The name is already registered with a different payload type.</exception>
    public static SessionEventType<TData> Register<TData>(string name, bool surfaceEligible = false)
        where TData : notnull
    {
        lock (Gate)
        {
            if (Entries.TryGetValue(name, out var existing))
            {
                if (existing.DataType != typeof(TData))
                {
                    throw new InvalidOperationException(
                        $"session event \"{name}\" is already registered with payload {existing.DataType.Name}");
                }
            }
            else
            {
                Entries[name] = new Entry(typeof(TData), surfaceEligible);
            }
        }

        return new SessionEventType<TData>(name);
    }

    /// <summary>
    /// Whether a name is in the vocabulary.
    /// </summary>
    /// <param name="name">The event name.</param>
    /// <returns>True when a reader can interpret events of this type.</returns>
    public static bool IsKnown(string name)
    {
        lock (Gate) return Entries.ContainsKey(name);
    }

    /// <summary>
    /// Whether events of one type may carry surface placement.
    /// </summary>
    /// <param name="name">The event name.</param>
    /// <returns>True for the message-producing types, false for every log-only one.</returns>
    public static bool IsSurfaceEligible(string name)
    {
        lock (Gate) return Entries.TryGetValue(name, out var entry) && entry.SurfaceEligible;
    }

    /// <summary>
    /// The payload type registered for a name.
    /// </summary>
    /// <param name="name">The event name.</param>
    /// <returns>The payload type, or null when the name is unknown.</returns>
    public static Type? PayloadType(string name)
    {
        lock (Gate) return Entries.TryGetValue(name, out var entry) ? entry.DataType : null;
    }

    /// <summary>Every registered event name, sorted.</summary>
    public static IReadOnlyList<string> KnownTypes
    {
        get
        {
            lock (Gate) return [.. Entries.Keys.OrderBy(static name => name, StringComparer.Ordinal)];
        }
    }
}

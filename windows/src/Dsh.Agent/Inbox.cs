using Dsh.Llm;
using Dsh.Session;

namespace Dsh.Agent;

/// <summary>Which boundary a message waits for.</summary>
public enum InboxTarget
{
    /// <summary>A queued prompt: it opens a turn of its own.</summary>
    NextTurn,

    /// <summary>Steering or injected context: it joins the next step of the running turn.</summary>
    NextStep,
}

/// <summary>One durable change to an agent's pending work.</summary>
/// <param name="Target">Which list changed.</param>
/// <param name="Start">Where in that list the change begins.</param>
/// <param name="RemovedCount">How many entries were taken out.</param>
/// <param name="Inserted">What was put in.</param>
/// <param name="Cancelled">
/// True when the removal discarded work rather than consuming it, which is how a
/// later reader tells a cancelled prompt from one a turn actually ran.
/// </param>
public sealed record InboxSplicedData(
    InboxTarget Target,
    int Start,
    int RemovedCount,
    IReadOnlyList<Message> Inserted,
    bool Cancelled = false);

/// <summary>
/// The agent's pending work, as a replayable projection of the log.
/// </summary>
/// <remarks>
/// Every change is written to the log before the live lists move, so a reload
/// reconstructs exactly the queue that existed — a queued prompt survives a restart,
/// and a cancelled one is visibly cancelled rather than silently missing.
/// </remarks>
public sealed class Inbox
{
    private readonly Session.Session _session;
    private readonly List<Message> _nextTurn = [];
    private readonly List<Message> _nextStep = [];

    /// <summary>The durable record of one inbox change.</summary>
    public static SessionEventType<InboxSplicedData> Spliced { get; } =
        SessionEventRegistry.Register<InboxSplicedData>("agent/inbox/spliced");

    /// <param name="session">The log this inbox projects and writes to.</param>
    public Inbox(Session.Session session)
    {
        _session = session;
        Replay();
    }

    /// <summary>Called when a message joins the inbox.</summary>
    public Action<Message>? Inserted { get; set; }

    /// <summary>Called when a message is dropped without being run.</summary>
    public Action<Message>? Discarded { get; set; }

    /// <summary>Called when a turn takes a message to run it.</summary>
    public Action<Message, int>? Claimed { get; set; }

    /// <summary>Prompts waiting to open a turn.</summary>
    public IReadOnlyList<Message> NextTurn => _nextTurn;

    /// <summary>Steering and context waiting for the next step boundary.</summary>
    public IReadOnlyList<Message> NextStep => _nextStep;

    /// <summary>Whether anything is waiting.</summary>
    public bool HasPending => _nextTurn.Count > 0 || _nextStep.Count > 0;

    private void Replay()
    {
        var events = _session.Events;
        var from = _session.Header.SeedLength ?? 0;
        for (var index = from; index < events.Count; index++)
        {
            if (!string.Equals(events[index].Type, Spliced.Name, StringComparison.Ordinal)) continue;
            var data = events[index].DataAs<InboxSplicedData>();
            var list = data.Target == InboxTarget.NextTurn ? _nextTurn : _nextStep;
            var start = Math.Clamp(data.Start, 0, list.Count);
            var remove = Math.Clamp(data.RemovedCount, 0, list.Count - start);
            list.RemoveRange(start, remove);
            list.InsertRange(start, data.Inserted);
        }
    }

    /// <summary>
    /// Change one of the lists, recording the change before applying it.
    /// </summary>
    /// <param name="target">Which list to change.</param>
    /// <param name="start">Where the change begins; clamped into range.</param>
    /// <param name="removeCount">How many entries to take out; clamped into range.</param>
    /// <param name="insert">What to put in.</param>
    /// <param name="cancelled">Whether a removal discards work rather than consuming it.</param>
    /// <returns>The entries that were removed.</returns>
    public IReadOnlyList<Message> Splice(
        InboxTarget target,
        int start,
        int removeCount,
        IReadOnlyList<Message>? insert = null,
        bool cancelled = false)
    {
        var list = target == InboxTarget.NextTurn ? _nextTurn : _nextStep;
        var inserted = insert ?? [];
        var clampedStart = Math.Clamp(start, 0, list.Count);
        var clampedRemove = Math.Clamp(removeCount, 0, list.Count - clampedStart);

        if (clampedRemove == 0 && inserted.Count == 0) return [];

        var removed = list.GetRange(clampedStart, clampedRemove);

        _session.Append(
            Spliced,
            new InboxSplicedData(target, clampedStart, clampedRemove, inserted, cancelled),
            ignorable: true);

        list.RemoveRange(clampedStart, clampedRemove);
        list.InsertRange(clampedStart, inserted);

        foreach (var message in inserted) Inserted?.Invoke(message);
        if (cancelled)
        {
            foreach (var message in removed) Discarded?.Invoke(message);
        }

        return removed;
    }

    /// <summary>
    /// Add a message to the end of one list.
    /// </summary>
    /// <param name="target">Which list to add to.</param>
    /// <param name="message">The message to add.</param>
    public void Append(InboxTarget target, Message message)
    {
        var list = target == InboxTarget.NextTurn ? _nextTurn : _nextStep;
        Splice(target, list.Count, 0, [message]);
    }

    /// <summary>
    /// Take the work one boundary is entitled to.
    /// </summary>
    /// <param name="target">
    /// The boundary being crossed. A step boundary claims only steering and context; a
    /// turn boundary claims those <em>and</em> exactly one queued prompt, in that order.
    /// </param>
    /// <param name="turn">The turn doing the claiming, for the notification.</param>
    /// <returns>The claimed messages, in the order they enter the step.</returns>
    /// <remarks>
    /// A claim is a plain removal with no cancellation marker, which is how a later
    /// reader distinguishes work a turn consumed from work a cancellation dropped.
    /// </remarks>
    public IReadOnlyList<Message> Claim(InboxTarget target, int turn)
    {
        var claimed = new List<Message>(Splice(InboxTarget.NextStep, 0, _nextStep.Count));
        if (target == InboxTarget.NextTurn)
        {
            claimed.AddRange(Splice(InboxTarget.NextTurn, 0, 1));
        }

        foreach (var message in claimed) Claimed?.Invoke(message, turn);
        return claimed;
    }

    /// <summary>Drop everything waiting, marking it discarded rather than consumed.</summary>
    public void Clear()
    {
        Splice(InboxTarget.NextStep, 0, _nextStep.Count, [], cancelled: true);
        Splice(InboxTarget.NextTurn, 0, _nextTurn.Count, [], cancelled: true);
    }

    /// <summary>
    /// Remove one queued message by id.
    /// </summary>
    /// <param name="id">The message to remove.</param>
    /// <returns>True when it was found and removed.</returns>
    public bool Remove(MessageId id)
    {
        foreach (var target in new[] { InboxTarget.NextTurn, InboxTarget.NextStep })
        {
            var list = target == InboxTarget.NextTurn ? _nextTurn : _nextStep;
            var index = list.FindIndex(message => message.Id.Equals(id));
            if (index < 0) continue;
            Splice(target, index, 1, [], cancelled: true);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Replace one queued message in place.
    /// </summary>
    /// <param name="id">The message to replace.</param>
    /// <param name="replacement">What to put there instead.</param>
    /// <returns>True when it was found and replaced.</returns>
    public bool Replace(MessageId id, Message replacement)
    {
        foreach (var target in new[] { InboxTarget.NextTurn, InboxTarget.NextStep })
        {
            var list = target == InboxTarget.NextTurn ? _nextTurn : _nextStep;
            var index = list.FindIndex(message => message.Id.Equals(id));
            if (index < 0) continue;
            Splice(target, index, 1, [replacement]);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Move one queued prompt to the front of the running turn's next step.
    /// </summary>
    /// <param name="id">The message to promote.</param>
    /// <returns>True when it was found and promoted.</returns>
    public bool Steer(MessageId id)
    {
        var index = _nextTurn.FindIndex(message => message.Id.Equals(id));
        if (index < 0) return false;
        var message = _nextTurn[index];
        Splice(InboxTarget.NextTurn, index, 1);
        Splice(InboxTarget.NextStep, _nextStep.Count, 0, [message]);
        return true;
    }
}

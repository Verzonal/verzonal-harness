using Dsh.Llm;

namespace Dsh.Session;

/// <summary>
/// The ordered, model-visible projection of the log: the seqs of the events that
/// become messages, in the order the model sees them.
/// </summary>
/// <remarks>
/// The surface is what makes "model-visible means logged" true by construction. An
/// event with no placement is invisible to the model no matter what it contains, and
/// a replacement drops the nodes it shadows from derivation while leaving every one
/// of them in the log.
/// </remarks>
public sealed class SurfaceState
{
    private readonly List<int> _nodes = [];

    /// <summary>The surface nodes, as event seqs in model order.</summary>
    public IReadOnlyList<int> Nodes => _nodes;

    /// <summary>
    /// How many positional replacements have landed. A consumer caching derived
    /// history compares this to know whether appending is enough or the cache must be
    /// rebuilt.
    /// </summary>
    public int ReplaceGeneration { get; private set; }

    internal void Append(int seq) => _nodes.Add(seq);

    internal void Replace(int seq, int startIndex, int endIndex)
    {
        _nodes.RemoveRange(startIndex, endIndex - startIndex + 1);
        _nodes.Insert(startIndex, seq);
        ReplaceGeneration++;
    }

    internal int IndexOf(int seq) => _nodes.IndexOf(seq);
}

/// <summary>What one event does to the surface, decided before the log is touched.</summary>
internal abstract record SurfacePlan;

/// <summary>The event is log-only and does not reach the model.</summary>
internal sealed record LogOnlyPlan : SurfacePlan
{
    public static LogOnlyPlan Instance { get; } = new();
}

/// <summary>The event joins the end of the surface.</summary>
internal sealed record AppendPlan(int Seq) : SurfacePlan;

/// <summary>The event replaces a contiguous run of nodes.</summary>
internal sealed record ReplacePlan(int Seq, int StartIndex, int EndIndex) : SurfacePlan;

/// <summary>
/// Folds events onto the surface, validating each placement before anything is
/// committed so a rejected append can never leave the surface half-updated.
/// </summary>
public sealed class SurfaceManager
{
    private readonly SurfaceState _state = new();

    /// <summary>The current surface.</summary>
    public SurfaceState State => _state;

    /// <summary>
    /// Check what an event would do to the surface without doing it.
    /// </summary>
    /// <param name="candidate">The event about to be appended.</param>
    /// <returns>The planned transition, to be committed with <see cref="Apply" />.</returns>
    /// <exception cref="InvalidOperationException">The placement is invalid.</exception>
    internal SurfacePlan Plan(SessionEvent candidate)
    {
        var eligible = SessionEventRegistry.IsSurfaceEligible(candidate.Type);

        if (!eligible)
        {
            if (candidate.SurfaceOp is not null || candidate.SourceEventSeqs is not null)
            {
                throw new InvalidOperationException(
                    $"session event \"{candidate.Type}\" is log-only and cannot carry surface placement");
            }

            return LogOnlyPlan.Instance;
        }

        if (candidate.SurfaceOp is null)
        {
            throw new InvalidOperationException(
                $"session event \"{candidate.Type}\" produces a message and must declare its surface placement");
        }

        switch (candidate.SurfaceOp)
        {
            case AppendOp:
                AssertProvenance(candidate, []);
                return new AppendPlan(candidate.Seq);

            case ReplaceOp replace:
            {
                var startIndex = _state.IndexOf(replace.Start);
                var endIndex = _state.IndexOf(replace.End);
                if (startIndex < 0 || endIndex < 0)
                {
                    throw new InvalidOperationException(
                        $"surface replacement [{replace.Start}, {replace.End}] names a seq that is not a current surface node");
                }

                if (startIndex > endIndex)
                {
                    throw new InvalidOperationException(
                        $"surface replacement [{replace.Start}, {replace.End}] is inverted");
                }

                var shadowed = _state.Nodes.Skip(startIndex).Take(endIndex - startIndex + 1).ToArray();
                AssertProvenance(candidate, shadowed);
                return new ReplacePlan(candidate.Seq, startIndex, endIndex);
            }

            default:
                throw new InvalidOperationException($"unsupported surface placement on \"{candidate.Type}\"");
        }
    }

    /// <summary>
    /// Commit a plan produced by <see cref="Plan" />.
    /// </summary>
    /// <param name="plan">The planned transition.</param>
    internal void Apply(SurfacePlan plan)
    {
        switch (plan)
        {
            case AppendPlan append:
                _state.Append(append.Seq);
                break;
            case ReplacePlan replace:
                _state.Replace(replace.Seq, replace.StartIndex, replace.EndIndex);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// A citation must name only earlier events, name each at most once, and account
    /// for every node a replacement shadows — otherwise history could lose a message
    /// with nothing recording where it went.
    /// </summary>
    private static void AssertProvenance(SessionEvent candidate, IReadOnlyList<int> shadowed)
    {
        var cited = candidate.SourceEventSeqs;
        if (cited is null)
        {
            if (shadowed.Count > 0)
            {
                throw new InvalidOperationException(
                    $"session event \"{candidate.Type}\" shadows surface nodes but cites no sources");
            }

            return;
        }

        var seen = new HashSet<int>();
        foreach (var seq in cited)
        {
            if (seq < 0 || seq >= candidate.Seq)
            {
                throw new InvalidOperationException(
                    $"session event \"{candidate.Type}\" cites seq {seq}, which is not an earlier event");
            }

            if (!seen.Add(seq))
            {
                throw new InvalidOperationException(
                    $"session event \"{candidate.Type}\" cites seq {seq} more than once");
            }
        }

        if (cited.Count == 0 && !string.Equals(candidate.Type, SessionEvents.AssistantMessage.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"session event \"{candidate.Type}\" cites an empty source set, which only an assistant message may do");
        }

        foreach (var seq in shadowed)
        {
            if (!seen.Contains(seq))
            {
                throw new InvalidOperationException(
                    $"session event \"{candidate.Type}\" shadows surface node {seq} without citing it");
            }
        }
    }
}

/// <summary>Projects surface events into the messages a model request carries.</summary>
public static class SurfaceProjection
{
    /// <summary>
    /// Turn one surface event into the message it contributes.
    /// </summary>
    /// <param name="entry">The event to project.</param>
    /// <returns>
    /// The message, or null when the event contributes none. An assistant message
    /// with empty content projects to null: it exists only to carry a truncated
    /// step's token accounting, and a content-less assistant turn must not enter the
    /// provider transcript.
    /// </returns>
    public static Message? Project(SessionEvent entry)
    {
        if (string.Equals(entry.Type, SessionEvents.UserMessage.Name, StringComparison.Ordinal))
        {
            return entry.DataAs<Message>();
        }

        if (string.Equals(entry.Type, SessionEvents.AssistantMessage.Name, StringComparison.Ordinal))
        {
            var data = entry.DataAs<AssistantMessageData>();
            return data.Message.Content.Count == 0 ? null : data.Message;
        }

        if (string.Equals(entry.Type, SessionEvents.ToolResult.Name, StringComparison.Ordinal))
        {
            return entry.DataAs<ToolResultData>().Message;
        }

        return null;
    }
}

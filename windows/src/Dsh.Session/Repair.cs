using Dsh.Llm;

namespace Dsh.Session;

/// <summary>
/// Closes a log that a crash left mid-turn.
/// </summary>
/// <remarks>
/// A log ending inside an open turn is not corrupt, it is unfinished: the events
/// before the crash are real and must survive. Reopening it appends the closers the
/// interrupted run never wrote — a result for every dispatched call, then the step,
/// then the turn — so the log satisfies the same relational contract a clean run
/// produces and the model never sees a call with no answer.
/// </remarks>
public static class SessionRepair
{
    /// <summary>Model-facing text for a call that was dispatched but whose outcome was lost.</summary>
    public const string OutcomeUnknownText = "Error: tool call outcome unknown after an interrupted session";

    /// <summary>Model-facing text for a call that was recorded but never dispatched.</summary>
    public const string NotStartedText = "Error: tool call did not start before the session was interrupted";

    /// <summary>The code recorded for a call whose outcome was lost.</summary>
    public const string OutcomeUnknownCode = "TOOL_OUTCOME_UNKNOWN";

    /// <summary>The code recorded for a call that never started.</summary>
    public const string NotStartedCode = "TOOL_NOT_STARTED";

    /// <summary>One synthetic closing event, ready to append.</summary>
    /// <param name="Type">The event's vocabulary name.</param>
    /// <param name="Data">The payload.</param>
    /// <param name="Intent">Surface placement, for the tool results.</param>
    public sealed record Closer(string Type, object Data, SurfaceIntent? Intent = null);

    /// <summary>
    /// Work out how to close an interrupted log.
    /// </summary>
    /// <param name="events">The stored log, in order.</param>
    /// <returns>
    /// The closers to append, in order, or an empty list when the log already ends
    /// cleanly. Timestamps are the caller's to assign: it reuses the last real
    /// event's time rather than inventing a later one, so repair never claims work
    /// happened after the crash.
    /// </returns>
    public static IReadOnlyList<Closer> InterruptedTurnClosers(IReadOnlyList<SessionEvent> events)
    {
        var openTurn = -1;
        var openStep = -1;
        var pending = new List<(CallId CallId, int Seq, bool Dispatched)>();

        foreach (var entry in events)
        {
            if (string.Equals(entry.Type, SessionEvents.TurnStart.Name, StringComparison.Ordinal))
            {
                openTurn = entry.DataAs<TurnStartData>().Turn;
                openStep = -1;
                pending.Clear();
            }
            else if (string.Equals(entry.Type, SessionEvents.TurnEnd.Name, StringComparison.Ordinal))
            {
                openTurn = -1;
                openStep = -1;
                pending.Clear();
            }
            else if (string.Equals(entry.Type, SessionEvents.StepStart.Name, StringComparison.Ordinal))
            {
                openStep = entry.DataAs<StepStartData>().Step;
                pending.Clear();
            }
            else if (string.Equals(entry.Type, SessionEvents.StepEnd.Name, StringComparison.Ordinal))
            {
                openStep = -1;
                pending.Clear();
            }
            else if (string.Equals(entry.Type, SessionEvents.ToolCall.Name, StringComparison.Ordinal))
            {
                var call = entry.DataAs<ToolCallData>();
                pending.Add((call.CallId, entry.Seq, true));
            }
            else if (string.Equals(entry.Type, SessionEvents.ToolResult.Name, StringComparison.Ordinal))
            {
                var result = entry.DataAs<ToolResultData>();
                if (result.Message.Content is [ToolResultBlock block])
                {
                    pending.RemoveAll(item => item.CallId == block.ToolCallId);
                }
            }
        }

        if (openTurn < 0) return [];

        var closers = new List<Closer>();

        foreach (var (callId, seq, dispatched) in pending)
        {
            var text = dispatched ? OutcomeUnknownText : NotStartedText;
            var code = dispatched ? OutcomeUnknownCode : NotStartedCode;
            closers.Add(new Closer(
                SessionEvents.ToolResult.Name,
                new ToolResultData(
                    openTurn,
                    openStep < 0 ? 1 : openStep,
                    Message.ToolResult(callId, [new TextBlock(text)], isError: true),
                    new ToolErrorInfo("InterruptedError", code)),
                new SurfaceIntent(AppendOp.Instance, [seq])));
        }

        if (openStep >= 0)
        {
            closers.Add(new Closer(SessionEvents.StepEnd.Name, new StepEndData(openTurn, openStep)));
        }

        closers.Add(new Closer(
            SessionEvents.TurnEnd.Name,
            new TurnEndData(openTurn, InterruptedTurnEnd.Instance)));

        return closers;
    }
}

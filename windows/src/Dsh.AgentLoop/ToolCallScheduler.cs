using System.Text.Json;
using Dsh.Cordis;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Tools;

namespace Dsh.AgentLoop;

/// <summary>
/// Runs one assistant step's tool calls.
/// </summary>
/// <remarks>
/// Exclusive calls form barriers; calls that declare themselves safe to overlap run
/// in a bounded rolling pool. Whatever the dispatch order, admission and results
/// commit in <b>model order</b>: the model sees its calls answered in the order it
/// made them, and a person is asked to approve them one at a time.
///
/// Cancellation drains the calls that started, commits their results, and then
/// records a synthetic result for every call it skipped. That matters because a
/// provider transcript with an unanswered tool call is invalid — the session would
/// be unresumable.
/// </remarks>
internal static class ToolCallScheduler
{
    /// <summary>Model-facing text for a call cancelled before it could start.</summary>
    public const string AbortedBeforeDispatchText = "Error: tool call aborted before dispatch";

    private sealed record PlannedCall(ToolCallBlock Block, ToolExecutionInput Input);

    private sealed record GroupOutcome(int Consumed, bool Aborted, bool Concluded);

    /// <summary>
    /// Run every call the model requested in this step.
    /// </summary>
    /// <param name="tools">The registry that admits and dispatches each call.</param>
    /// <param name="session">The log every call and result is written to.</param>
    /// <param name="scope">The agent boundary the calls run under.</param>
    /// <param name="turn">The enclosing turn.</param>
    /// <param name="step">The enclosing step.</param>
    /// <param name="calls">The calls, in model order.</param>
    /// <param name="maxParallel">How many overlapping calls the pool allows.</param>
    /// <param name="acceptContext">Receives context a tool staged for the next step.</param>
    /// <param name="cancellationToken">Cancels the batch.</param>
    /// <returns>
    /// Whether any committed result asked the loop to stop rather than take another step.
    /// </returns>
    public static async Task<bool> ExecuteAsync(
        ToolRuntime tools,
        Session.Session session,
        ScopeKey scope,
        int turn,
        int step,
        IReadOnlyList<ToolCallBlock> calls,
        int maxParallel,
        Action<Message> acceptContext,
        CancellationToken cancellationToken)
    {
        var planned = new List<PlannedCall>(calls.Count);
        foreach (var block in calls)
        {
            planned.Add(new PlannedCall(
                block,
                new ToolExecutionInput(block.Id, block.Name, ParseArguments(block.Arguments), scope)));
        }

        var next = 0;
        var concluded = false;

        while (next < planned.Count)
        {
            // Classified fresh each time, so a registry change between groups can turn
            // a later call into a barrier.
            var mode = tools.ExecutionMode(planned[next].Input);
            var group = mode == ToolExecutionMode.Parallel
                ? planned.GetRange(next, planned.Count - next)
                : [planned[next]];

            var outcome = await RunGroupAsync(
                tools, session, turn, step, group, mode, maxParallel, acceptContext, cancellationToken);

            next += outcome.Consumed;
            concluded |= outcome.Concluded;

            if (outcome.Aborted)
            {
                for (var index = next; index < planned.Count; index++)
                {
                    AppendSkipped(session, turn, step, planned[index].Block);
                }

                return concluded;
            }
        }

        return concluded;
    }

    private static async Task<GroupOutcome> RunGroupAsync(
        ToolRuntime tools,
        Session.Session session,
        int turn,
        int step,
        IReadOnlyList<PlannedCall> group,
        ToolExecutionMode mode,
        int maxParallel,
        Action<Message> acceptContext,
        CancellationToken cancellationToken)
    {
        var slots = new ToolExecutionResult?[group.Count];
        var callSeqs = new int[group.Count];
        Array.Fill(callSeqs, -1);

        var inFlight = new Dictionary<int, Task<int>>();
        var nextToStart = 0;
        var started = 0;
        var committed = 0;
        var concluded = false;
        var aborted = cancellationToken.IsCancellationRequested;

        void CommitReady()
        {
            while (committed < group.Count && slots[committed] is { } result)
            {
                AppendResult(session, turn, step, group[committed].Block, result, callSeqs[committed]);
                foreach (var context in result.AdditionalContexts ?? []) acceptContext(context);
                concluded |= result.ConcludesTurn;
                committed++;
            }
        }

        async Task StartCallAsync(int index)
        {
            var call = group[index];
            callSeqs[index] = AppendCall(session, turn, step, call.Block);
            started++;

            // Admission is awaited in model order even when bodies overlap, so two
            // parallel calls never put two approval prompts up at once.
            var admission = await tools.AdmitAsync(call.Input, cancellationToken);
            if (admission is ToolRefused refused)
            {
                slots[index] = refused.Result;
                return;
            }

            var execution = ((ToolAdmitted)admission).Execution;
            inFlight[index] = DispatchAsync(index, execution);

            async Task<int> DispatchAsync(int slot, ToolExecution exec)
            {
                slots[slot] = await tools.DispatchAsync(exec, cancellationToken);
                return slot;
            }
        }

        async Task FillPoolAsync()
        {
            while (!aborted && nextToStart < group.Count && inFlight.Count < maxParallel)
            {
                if (nextToStart > 0
                    && mode == ToolExecutionMode.Parallel
                    && tools.ExecutionMode(group[nextToStart].Input) != ToolExecutionMode.Parallel)
                {
                    break;
                }

                await StartCallAsync(nextToStart);
                nextToStart++;
                CommitReady();
                if (cancellationToken.IsCancellationRequested) aborted = true;
            }
        }

        await FillPoolAsync();
        while (inFlight.Count > 0)
        {
            var settled = await Task.WhenAny(inFlight.Values);
            inFlight.Remove(await settled);
            CommitReady();
            if (cancellationToken.IsCancellationRequested) aborted = true;
            await FillPoolAsync();
        }

        if (aborted)
        {
            // Started calls have committed by now; every call this group never began
            // still needs an answer, or the transcript is unresumable.
            for (var index = started; index < group.Count; index++)
            {
                AppendSkipped(session, turn, step, group[index].Block);
            }

            return new GroupOutcome(group.Count, true, concluded);
        }

        return new GroupOutcome(started, false, concluded);
    }

    /// <summary>
    /// Turn the model's raw argument string into a value the pipeline can work with.
    /// </summary>
    /// <param name="raw">Exactly what the model produced.</param>
    /// <returns>
    /// The parsed value, an empty object for an empty string, or the raw text itself
    /// when the model's JSON was malformed — schema validation then tells the model
    /// precisely what was wrong instead of the call vanishing.
    /// </returns>
    internal static JsonValue ParseArguments(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return JsonValue.From(new Dictionary<string, object?>());
        try
        {
            return JsonValue.From(JsonDocument.Parse(raw).RootElement.Clone());
        }
        catch (JsonException)
        {
            return new JsonString(raw);
        }
    }

    private static int AppendCall(Session.Session session, int turn, int step, ToolCallBlock block)
        => session.Append(
            SessionEvents.ToolCall,
            new ToolCallData(turn, step, block.Id, block.Name, block.Arguments)).Seq;

    private static void AppendResult(
        Session.Session session,
        int turn,
        int step,
        ToolCallBlock block,
        ToolExecutionResult result,
        int callSeq)
        => session.Append(
            SessionEvents.ToolResult,
            new ToolResultData(
                turn,
                step,
                Message.ToolResult(block.Id, result.Content, result.IsError),
                result.Error,
                result.Meta),
            new SurfaceIntent(AppendOp.Instance, [callSeq]));

    private static void AppendSkipped(Session.Session session, int turn, int step, ToolCallBlock block)
    {
        var callSeq = AppendCall(session, turn, step, block);
        AppendResult(
            session,
            turn,
            step,
            block,
            new ToolExecutionResult(
                [new TextBlock(AbortedBeforeDispatchText)],
                IsError: true,
                Error: new ToolErrorInfo("AbortError", ToolErrorCodes.AbortedBeforeDispatch)),
            callSeq);
    }
}

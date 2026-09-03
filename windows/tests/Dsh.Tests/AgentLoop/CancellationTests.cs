using Dsh.Agent;
using Dsh.Llm;
using Dsh.Llm.Fake;
using Dsh.Session;
using Dsh.Tools;

namespace Dsh.Tests.AgentLoop;

public sealed class CancellationTests
{
    private static ToolCallBlock Call(string id, string name)
        => new(new CallId(id), name, "{}");

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail($"timed out waiting for {what}");
    }

    [Fact]
    public async Task Cancelling_mid_stream_closes_the_turn_as_aborted_with_the_cause()
    {
        var adapter = new ScriptedAdapter(ScriptedReply.Text("this answer will be cut short"))
        {
            ChunkDelay = TimeSpan.FromMilliseconds(30),
        };
        await using var fixture = await LoopFixture.StartAsync(adapter);

        fixture.Agent.Followup(Message.UserText("hello"));
        await WaitUntilAsync(
            () => fixture.Session.Events.Any(static entry => entry.Type == "assistant/chunk"),
            "the stream to start");

        fixture.Agent.Cancel(UserCancel.Instance);
        await fixture.Agent.WhenIdleAsync();

        var reason = Assert.IsType<AbortedTurnEnd>(fixture.LastTurnEnd());
        Assert.IsType<UserCancel>(reason.Reason);
    }

    [Fact]
    public async Task What_the_model_had_already_delivered_is_recorded_as_interrupted()
    {
        var adapter = new ScriptedAdapter(ScriptedReply.Text("one two three four five six"))
        {
            ChunkDelay = TimeSpan.FromMilliseconds(30),
        };
        await using var fixture = await LoopFixture.StartAsync(adapter);

        fixture.Agent.Followup(Message.UserText("hello"));
        await WaitUntilAsync(
            () => fixture.Session.Events.Count(static entry => entry.Type == "assistant/chunk") >= 3,
            "a few chunks to arrive");

        fixture.Agent.Cancel(UserCancel.Instance);
        await fixture.Agent.WhenIdleAsync();

        var message = fixture.Session.Events
            .SingleOrDefault(static entry => entry.Type == SessionEvents.AssistantMessage.Name);

        Assert.NotNull(message);
        var data = message!.DataAs<AssistantMessageData>();
        Assert.True(data.Interrupted);
        Assert.StartsWith("one", data.Message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_batch_still_answers_every_call_the_model_made()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(
            ScriptedReply.ToolCalls([Call("call-1", "blocker"), Call("call-2", "never"), Call("call-3", "never")])));

        var started = new TaskCompletionSource();
        fixture.Tools.Register(fixture.Ctx, new ProbeTool("blocker", async (_, exec) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, exec.CancellationToken);
            return ProbeTool.Text("unreachable");
        }));
        fixture.Tools.Register(fixture.Ctx, new ProbeTool("never", (_, _) =>
            Task.FromResult(ProbeTool.Text("should not run"))));

        fixture.Agent.Followup(Message.UserText("run them"));
        await started.Task;
        fixture.Agent.Cancel(UserCancel.Instance);
        await fixture.Agent.WhenIdleAsync();

        var calls = fixture.Session.Events.Count(static entry => entry.Type == SessionEvents.ToolCall.Name);
        var results = fixture.Session.Events.Count(static entry => entry.Type == SessionEvents.ToolResult.Name);

        // A provider transcript with an unanswered call is invalid, so the skipped
        // calls are answered too.
        Assert.Equal(3, calls);
        Assert.Equal(3, results);
    }

    [Fact]
    public async Task A_call_skipped_by_cancellation_says_it_never_started()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(
            ScriptedReply.ToolCalls([Call("call-1", "blocker"), Call("call-2", "never")])));

        var started = new TaskCompletionSource();
        fixture.Tools.Register(fixture.Ctx, new ProbeTool("blocker", async (_, exec) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, exec.CancellationToken);
            return ProbeTool.Text("unreachable");
        }));
        fixture.Tools.Register(fixture.Ctx, new ProbeTool("never", (_, _) =>
            Task.FromResult(ProbeTool.Text("should not run"))));

        fixture.Agent.Followup(Message.UserText("run them"));
        await started.Task;
        fixture.Agent.Cancel(UserCancel.Instance);
        await fixture.Agent.WhenIdleAsync();

        var codes = fixture.Session.Events
            .Where(static entry => entry.Type == SessionEvents.ToolResult.Name)
            .Select(static entry => entry.DataAs<ToolResultData>().Error?.Code)
            .ToArray();

        Assert.Contains(ToolErrorCodes.AbortedBeforeDispatch, codes);
    }

    [Fact]
    public async Task Cancelling_discards_queued_work_by_default()
    {
        var adapter = new ScriptedAdapter(ScriptedReply.Text("first"), ScriptedReply.Text("second"))
        {
            ChunkDelay = TimeSpan.FromMilliseconds(30),
        };
        await using var fixture = await LoopFixture.StartAsync(adapter);

        fixture.Agent.Followup(Message.UserText("one"));
        fixture.Agent.Followup(Message.UserText("two"));
        await WaitUntilAsync(
            () => fixture.Session.Events.Any(static entry => entry.Type == "assistant/chunk"),
            "the first turn to start streaming");

        fixture.Agent.Cancel(UserCancel.Instance);
        await fixture.Agent.WhenIdleAsync();

        Assert.False(fixture.Agent.Inbox.HasPending);
        Assert.Equal(AgentStatus.Idle, fixture.Agent.Status);
    }

    [Fact]
    public async Task Cancelling_can_keep_queued_work_for_the_next_turn()
    {
        var adapter = new ScriptedAdapter(ScriptedReply.Text("first"), ScriptedReply.Text("second"))
        {
            ChunkDelay = TimeSpan.FromMilliseconds(30),
        };
        await using var fixture = await LoopFixture.StartAsync(adapter);

        fixture.Agent.Followup(Message.UserText("one"));
        await WaitUntilAsync(
            () => fixture.Session.Events.Any(static entry => entry.Type == "assistant/chunk"),
            "the first turn to start streaming");
        fixture.Agent.Followup(Message.UserText("two"));

        fixture.Agent.Cancel(UserCancel.Instance, new CancelOptions(KeepInbox: true));
        await fixture.Agent.WhenIdleAsync();

        Assert.True(fixture.Agent.Inbox.HasPending);
    }

    [Fact]
    public async Task Cancelling_an_idle_agent_does_nothing_and_does_not_arm_later_work()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(ScriptedReply.Text("ok")));

        fixture.Agent.Cancel(UserCancel.Instance);
        await fixture.Agent.WhenIdleAsync();

        Assert.DoesNotContain(fixture.Session.Events, static entry => entry.Type == SessionEvents.TurnStart.Name);

        await fixture.PromptAsync("hello");
        Assert.IsType<CompletedTurnEnd>(fixture.LastTurnEnd());
    }

    [Fact]
    public async Task Status_transitions_are_announced_once_each_way()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(ScriptedReply.Text("ok")));
        var seen = new List<AgentStatus>();
        fixture.Ctx.WithScope(fixture.Agent.Scope).On(AgentKeys.Status, notice => seen.Add(notice.Status));

        await fixture.PromptAsync("hello");

        Assert.Equal([AgentStatus.Running, AgentStatus.Idle], seen);
    }

    [Fact]
    public async Task Maintenance_reads_as_idle_and_the_wake_it_latched_replays_afterwards()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(ScriptedReply.Text("after maintenance")));

        var release = new TaskCompletionSource();
        var maintenance = fixture.Agent.RunMaintenanceAsync(async _ =>
        {
            await release.Task;
            return 0;
        });

        Assert.Equal(AgentStatus.Idle, fixture.Agent.Status);

        fixture.Agent.Followup(Message.UserText("waiting behind maintenance"));
        Assert.Empty(fixture.Adapter.Requests);

        release.SetResult();
        await maintenance;
        await fixture.Agent.WhenIdleAsync();

        Assert.Single(fixture.Adapter.Requests);
        Assert.IsType<CompletedTurnEnd>(fixture.LastTurnEnd());
    }

    [Fact]
    public async Task Maintenance_refuses_to_start_while_a_turn_is_running()
    {
        var adapter = new ScriptedAdapter(ScriptedReply.Text("streaming"))
        {
            ChunkDelay = TimeSpan.FromMilliseconds(30),
        };
        await using var fixture = await LoopFixture.StartAsync(adapter);

        fixture.Agent.Followup(Message.UserText("hello"));
        await WaitUntilAsync(
            () => fixture.Agent.Status == AgentStatus.Running,
            "the turn to start");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Agent.RunMaintenanceAsync(_ => Task.FromResult(0)));

        await fixture.Agent.WhenIdleAsync();
    }
}

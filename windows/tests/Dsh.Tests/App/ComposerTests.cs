using Dsh.App.Core;
using Dsh.Llm;
using Dsh.Llm.Fake;
using Dsh.Session;
using Dsh.Tests.AgentLoop;

namespace Dsh.Tests.App;

/// <summary>
/// The composer over a real agent — xUnit builds one instance per test, so each test
/// gets its own harness and its own empty inbox.
/// </summary>
/// <remarks>
/// The queue and steer assertions run against a <em>busy</em> agent, because that is
/// the only state in which the two are distinguishable: an idle agent claims a queued
/// prompt the instant it arrives, so reading the queue afterwards would be a race
/// against the turn it just started.
/// </remarks>
public sealed class ComposerTests : IAsyncLifetime
{
    private readonly TaskCompletionSource _entered = new();
    private readonly TaskCompletionSource _release = new();

    private LoopFixture _fixture = null!;
    private ComposerViewModel _composer = null!;

    public async Task InitializeAsync()
    {
        _fixture = await LoopFixture.StartAsync(new ScriptedAdapter(
            ScriptedReply.ToolCalls([new ToolCallBlock(new CallId("call-1"), "hold", "{}")]),
            ScriptedReply.Text("done")));

        _fixture.Tools.Register(_fixture.Ctx, new ProbeTool("hold", async (_, exec) =>
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(exec.CancellationToken);
            return ProbeTool.Text("held");
        }));

        _composer = new ComposerViewModel(() => _fixture.Agent) { HasWorkspace = true, Draft = "hello" };
    }

    public async Task DisposeAsync()
    {
        _release.TrySetResult();
        await _fixture.DisposeAsync();
    }

    /// <summary>Open a turn and wait inside the tool, leaving the agent running.</summary>
    private async Task GoBusyAsync()
    {
        _fixture.Agent.Followup(Message.UserText("start something"));
        await _entered.Task;
        _composer.IsRunning = true;
    }

    [Fact]
    public void Shift_enter_is_a_newline_even_when_nothing_else_is_ready()
    {
        var orphan = new ComposerViewModel(static () => null);

        Assert.Equal(ComposerAction.Newline, orphan.ResolveEnter(shift: true, control: false));
    }

    [Fact]
    public void Without_an_agent_the_composer_refuses_input()
    {
        var orphan = new ComposerViewModel(static () => null) { HasWorkspace = true, Draft = "hello" };

        Assert.False(orphan.IsEnabled);
        Assert.Equal(ComposerAction.None, orphan.ResolveEnter(shift: false, control: false));
    }

    [Fact]
    public void Without_a_workspace_the_composer_refuses_input()
    {
        _composer.HasWorkspace = false;

        Assert.False(_composer.IsEnabled);
        Assert.Equal(ComposerAction.None, _composer.ResolveEnter(shift: false, control: false));
        Assert.Contains("workspace", _composer.Placeholder, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_empty_draft_sends_nothing()
    {
        _composer.Draft = "   ";

        Assert.Equal(ComposerAction.None, _composer.ResolveEnter(shift: false, control: false));
        Assert.False(_composer.Submit(ComposerAction.Queue));
    }

    [Fact]
    public void While_idle_both_keys_queue_because_there_is_nothing_to_steer()
    {
        Assert.Equal(ComposerAction.Queue, _composer.ResolveEnter(shift: false, control: false));
        Assert.Equal(ComposerAction.Queue, _composer.ResolveEnter(shift: false, control: true));
    }

    [Fact]
    public void While_running_enter_queues_and_control_enter_steers()
    {
        _composer.IsRunning = true;

        Assert.Equal(ComposerAction.Queue, _composer.ResolveEnter(shift: false, control: false));
        Assert.Equal(ComposerAction.Steer, _composer.ResolveEnter(shift: false, control: true));
    }

    [Fact]
    public void The_preference_swaps_which_key_does_which()
    {
        _composer.IsRunning = true;
        _composer.BusyEnter = BusyEnterBehavior.Steer;

        Assert.Equal(ComposerAction.Steer, _composer.ResolveEnter(shift: false, control: false));
        Assert.Equal(ComposerAction.Queue, _composer.ResolveEnter(shift: false, control: true));
    }

    [Fact]
    public void Being_blocked_disables_the_composer_and_says_why()
    {
        _composer.IsBlocked = true;
        _composer.BlockedReason = "Waiting for approval.";

        Assert.False(_composer.IsEnabled);
        Assert.Equal("Waiting for approval.", _composer.Placeholder);
    }

    [Fact]
    public async Task Queueing_puts_the_message_in_the_next_turn_and_clears_the_draft()
    {
        await GoBusyAsync();

        Assert.True(_composer.Submit(ComposerAction.Queue));

        Assert.Equal(string.Empty, _composer.Draft);
        var queued = Assert.Single(_composer.Queued);
        Assert.Equal("hello", ContentBlocks.FlattenText(queued.Content));
        Assert.Empty(_composer.Steering);
    }

    [Fact]
    public async Task Steering_puts_the_message_in_the_next_step_instead()
    {
        await GoBusyAsync();

        Assert.True(_composer.Submit(ComposerAction.Steer));

        Assert.Single(_composer.Steering);
        Assert.Empty(_composer.Queued);
    }

    [Fact]
    public async Task The_resolved_action_is_the_one_that_gets_carried_out()
    {
        await GoBusyAsync();

        Assert.True(_composer.Submit(_composer.ResolveEnter(shift: false, control: true)));

        Assert.Single(_composer.Steering);
    }

    [Fact]
    public async Task A_queued_message_can_be_edited_promoted_or_dropped()
    {
        await GoBusyAsync();

        _composer.Submit(ComposerAction.Queue);
        _composer.Draft = "second";
        _composer.Submit(ComposerAction.Queue);

        var first = _composer.Queued[0].Id;
        var second = _composer.Queued[1].Id;

        Assert.True(_composer.Edit(first, "edited"));
        Assert.Equal("edited", ContentBlocks.FlattenText(_composer.Queued[0].Content));

        Assert.True(_composer.Promote(second));
        Assert.Single(_composer.Steering);

        Assert.True(_composer.Remove(_composer.Queued[0].Id));
        Assert.Empty(_composer.Queued);
    }

    [Fact]
    public async Task Stopping_cancels_the_running_turn_and_discards_what_was_queued()
    {
        await GoBusyAsync();

        _composer.Draft = "queued behind it";
        _composer.Submit(ComposerAction.Queue);
        _composer.Stop();
        await _fixture.Agent.WhenIdleAsync();

        Assert.Empty(_composer.Queued);
        Assert.IsType<AbortedTurnEnd>(_fixture.LastTurnEnd());
    }
}

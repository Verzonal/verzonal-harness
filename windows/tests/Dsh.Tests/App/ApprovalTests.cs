using Dsh.App.Core;
using Dsh.Cordis;
using Dsh.Interaction;
using Dsh.Tools;

namespace Dsh.Tests.App;

/// <summary>
/// The approval takeover against the real approval service, so what a test asserts is
/// what a tool would actually be told.
/// </summary>
public sealed class ApprovalTests : IAsyncLifetime
{
    private Context _ctx = null!;
    private Fiber _approvals = null!;

    public async Task InitializeAsync()
    {
        _ctx = Context.CreateRoot();
        _approvals = _ctx.Plugin(ApprovalService.Plugin());
        await _approvals.WhenSettledAsync();
    }

    public async Task DisposeAsync() => await _approvals.DisposeAsync();

    private IApprovalService Service => _ctx.Require<IApprovalService>(ApprovalKeys.Service);

    private static ApprovalRequest Request(string reason = "it needs write access")
        => new("write", "call-1", reason);

    /// <summary>Asks, waits for the panel to appear, then hands it back for answering.</summary>
    private async Task<(Task<ApprovalOutcome> Answer, ApprovalViewModel Panel)> AskAsync(
        ApprovalViewModel panel,
        CancellationToken cancellationToken = default)
    {
        var answer = Service.RequestAsync(Request(), cancellationToken);
        await WaitForAsync(() => panel.IsWaiting);
        return (answer, panel);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++) await Task.Delay(5);
        Assert.True(condition(), "the condition never became true");
    }

    [Fact]
    public async Task With_no_answerer_registered_everything_is_refused()
    {
        Assert.Equal(ApprovalOutcome.Unavailable, await Service.RequestAsync(Request(), default));
    }

    [Fact]
    public async Task Asking_raises_the_takeover_with_the_models_own_words()
    {
        using var panel = new ApprovalViewModel(_ctx);

        var (answer, _) = await AskAsync(panel);

        Assert.True(panel.IsWaiting);
        Assert.Equal("write", panel.Question!.ToolName);
        Assert.Equal("it needs write access", panel.Question.Reason);
        Assert.Equal("call-1", panel.Question.Detail);

        panel.Reject();
        await answer;
    }

    [Fact]
    public async Task Allowing_once_grants_only_that_action_and_lowers_the_panel()
    {
        using var panel = new ApprovalViewModel(_ctx);
        var (answer, _) = await AskAsync(panel);

        panel.AllowOnce();

        Assert.Equal(ApprovalOutcome.AllowedOnce, await answer);
        Assert.False(panel.IsWaiting);

        // The next ask starts from nothing: there is no standing grant to inherit.
        var second = Service.RequestAsync(Request(), default);
        await WaitForAsync(() => panel.IsWaiting);
        panel.Reject();
        Assert.Equal(ApprovalOutcome.Rejected, await second);
    }

    [Fact]
    public async Task Rejecting_refuses_the_action()
    {
        using var panel = new ApprovalViewModel(_ctx);
        var (answer, _) = await AskAsync(panel);

        panel.Reject();

        Assert.Equal(ApprovalOutcome.Rejected, await answer);
        Assert.False(panel.IsWaiting);
    }

    [Fact]
    public async Task A_withdrawn_question_lowers_the_panel_by_itself()
    {
        using var panel = new ApprovalViewModel(_ctx);
        using var withdrawal = new CancellationTokenSource();
        var (answer, _) = await AskAsync(panel, withdrawal.Token);

        await withdrawal.CancelAsync();

        Assert.Equal(ApprovalOutcome.Cancelled, await answer);
        await WaitForAsync(() => !panel.IsWaiting);
    }

    [Fact]
    public async Task Closing_the_app_while_a_question_is_up_refuses_rather_than_hangs()
    {
        var panel = new ApprovalViewModel(_ctx);
        var (answer, _) = await AskAsync(panel);

        panel.Dispose();

        Assert.Equal(ApprovalOutcome.Cancelled, await answer);
    }

    [Fact]
    public async Task Unregistering_the_panel_leaves_later_questions_refused()
    {
        var panel = new ApprovalViewModel(_ctx);
        panel.Dispose();

        Assert.Equal(ApprovalOutcome.Unavailable, await Service.RequestAsync(Request(), default));
    }

    [Fact]
    public async Task The_never_policy_refuses_before_the_panel_can_appear()
    {
        using var mounted = _ctx.Provide(SandboxKeys.Service, new FixedApproval(ApprovalPolicy.Never));
        using var panel = new ApprovalViewModel(_ctx);

        var outcome = await Service.RequestAsync(Request(), default);

        Assert.Equal(ApprovalOutcome.Rejected, outcome);
        Assert.False(panel.IsWaiting);
    }

    /// <summary>A policy whose only job is to pin the approval knob.</summary>
    private sealed class FixedApproval : ISandboxPolicy
    {
        public FixedApproval(ApprovalPolicy policy)
            => State = new SandboxState(SandboxMode.WorkspaceWrite, policy, "/workspace");

        public SandboxState State { get; }

        public string? RefuseWrite(string fullPath) => null;

        public bool CommandNeedsApproval() => true;
    }

    [Fact]
    public async Task An_answer_that_arrives_with_nothing_pending_is_ignored()
    {
        using var panel = new ApprovalViewModel(_ctx);

        panel.AllowOnce();
        panel.Reject();

        Assert.False(panel.IsWaiting);

        // And the channel still works afterwards.
        var (answer, _) = await AskAsync(panel);
        panel.AllowOnce();
        Assert.Equal(ApprovalOutcome.AllowedOnce, await answer);
    }
}

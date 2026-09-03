using Dsh.Agent;
using Dsh.Cordis;
using Dsh.Session;
using Dsh.Tools;

namespace Dsh.Interaction;

/// <summary>One question put to a person.</summary>
/// <param name="ApprovalId">Pairs the question with its answer.</param>
/// <param name="Request">What is being asked.</param>
/// <param name="CancellationToken">Withdraws the question.</param>
public sealed record ApprovalQuestion(
    string ApprovalId,
    ApprovalRequest Request,
    CancellationToken CancellationToken);

/// <summary>Event keys the approval capability publishes.</summary>
public static class ApprovalEvents
{
    /// <summary>
    /// Asks whoever can answer. A front-end registers here; nothing else does.
    /// </summary>
    public static WaterfallKey<ApprovalQuestion, ApprovalOutcome> Request { get; } = new("approval/request");
}

/// <summary>
/// Puts privileged actions to a person.
/// </summary>
/// <remarks>
/// Fails closed at every edge. No answerer, an answerer that throws, or a withdrawn
/// question all refuse — and the <c>never</c> policy short-circuits to a refusal
/// <em>before</em> any listener runs, so no listener can turn "never ask" into
/// "always allow".
/// </remarks>
public sealed class ApprovalService : Service, IApprovalService
{
    private readonly Func<Session.Session?> _session;

    /// <param name="ctx">The mounting plugin's context.</param>
    /// <param name="session">Reads the session the audit trail is written to.</param>
    public ApprovalService(Context ctx, Func<Session.Session?> session) : base(ctx, ApprovalKeys.Service)
    {
        PermissionEvents.EnsureRegistered();
        _session = session;
    }

    /// <inheritdoc />
    public async Task<ApprovalOutcome> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        var policy = Ctx.Get<ISandboxPolicy>(SandboxKeys.Service)?.State.Approval ?? ApprovalPolicy.Ask;
        if (policy == ApprovalPolicy.Never) return ApprovalOutcome.Rejected;

        var approvalId = Guid.NewGuid().ToString("N")[..12];
        var session = _session();

        session?.Append(
            PermissionEvents.Asked,
            new ApprovalAskedData(approvalId, request.ToolName, request.CallId, request.Reason),
            ignorable: true);

        ApprovalOutcome outcome;
        try
        {
            outcome = await Ctx.WaterfallAsync(
                ApprovalEvents.Request,
                new ApprovalQuestion(approvalId, request, cancellationToken),
                () => Task.FromResult(ApprovalOutcome.Unavailable));
        }
        catch (OperationCanceledException)
        {
            outcome = ApprovalOutcome.Cancelled;
        }
        catch (Exception error)
        {
            Ctx.Logger.Log(LogLevel.Warn, ApprovalKeys.Service, "an approval answerer failed", error);
            outcome = ApprovalOutcome.Unavailable;
        }

        session?.Append(
            PermissionEvents.Decided,
            new ApprovalDecidedData(approvalId, outcome),
            ignorable: true);

        return outcome;
    }

    /// <summary>Mount the approval capability over the initiating agent's session.</summary>
    /// <returns>The plugin to hand to <see cref="Context.Plugin" />.</returns>
    public static IPlugin Plugin()
        => ServicePlugin.Create<ApprovalService>(
            "user-approval",
            ApprovalKeys.Service,
            ctx => new ApprovalService(ctx, () => AgentRegistry.CurrentInitiator?.Session));
}

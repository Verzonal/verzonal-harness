namespace Dsh.Tools;

/// <summary>
/// How an approval request settled. A closed set, and every caller fails closed:
/// anything that is not an explicit grant refuses the action.
/// </summary>
public enum ApprovalOutcome
{
    /// <summary>Granted for this action only. The only grant there is.</summary>
    AllowedOnce,

    /// <summary>Explicitly refused.</summary>
    Rejected,

    /// <summary>Withdrawn before anyone answered.</summary>
    Cancelled,

    /// <summary>Nobody could be asked, so the action is refused.</summary>
    Unavailable,
}

/// <summary>One request to put an action to a person.</summary>
/// <param name="ToolName">The tool asking.</param>
/// <param name="CallId">The call it belongs to, so a UI can pair the request with the visible call.</param>
/// <param name="Reason">Why approval is being asked for, written for a person.</param>
public sealed record ApprovalRequest(string ToolName, string? CallId = null, string? Reason = null);

/// <summary>
/// The approval capability's Service Definition, declared here because the tool
/// pipeline is its consumer.
/// </summary>
/// <remarks>
/// Deliberately narrow: there is one grant, it covers one action, and it does not
/// persist. Anything durable about permission is a policy knob recorded in the
/// session log, not an allowlist accumulated here.
/// </remarks>
public interface IApprovalService
{
    /// <summary>
    /// Put one action to a person.
    /// </summary>
    /// <param name="request">What is being asked.</param>
    /// <param name="cancellationToken">Withdraws the request.</param>
    /// <returns>
    /// The outcome. Implementations fail closed: no answerer, a failing answerer, or
    /// a withdrawn request all refuse rather than allow.
    /// </returns>
    Task<ApprovalOutcome> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>The context key the approval capability is published under.</summary>
public static class ApprovalKeys
{
    /// <summary>The context key an approval provider claims.</summary>
    public const string Service = "approval";
}

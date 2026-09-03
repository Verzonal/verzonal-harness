using Dsh.Session;
using Dsh.Tools;

namespace Dsh.Interaction;

/// <summary>The sandbox mode a session switched to.</summary>
/// <param name="Mode">The new mode.</param>
public sealed record SandboxModeData(SandboxMode Mode);

/// <summary>The approval policy a session switched to.</summary>
/// <param name="Policy">The new policy.</param>
public sealed record ApprovalPolicyData(ApprovalPolicy Policy);

/// <summary>The named preset a person selected.</summary>
/// <param name="Preset">The preset's name.</param>
public sealed record PermissionPresetData(string Preset);

/// <summary>One approval request, recorded for audit.</summary>
/// <param name="ApprovalId">Pairs the question with its answer.</param>
/// <param name="ToolName">The tool that asked.</param>
/// <param name="CallId">The call it belongs to.</param>
/// <param name="Reason">Why approval was asked for.</param>
public sealed record ApprovalAskedData(string ApprovalId, string ToolName, string? CallId, string? Reason);

/// <summary>How an approval request was answered.</summary>
/// <param name="ApprovalId">The question this answers.</param>
/// <param name="Outcome">What was decided.</param>
public sealed record ApprovalDecidedData(string ApprovalId, ApprovalOutcome Outcome);

/// <summary>
/// The durable permission vocabulary.
/// </summary>
/// <remarks>
/// Policy is recorded rather than held in memory so a resumed session restores the
/// settings it was running under. A session that silently came back with wider
/// permissions than it had would be a surprise in exactly the wrong direction.
/// </remarks>
public static class PermissionEvents
{
    /// <summary>A change to how much the tools may touch.</summary>
    public static SessionEventType<SandboxModeData> SandboxMode { get; } =
        SessionEventRegistry.Register<SandboxModeData>("sandbox/mode");

    /// <summary>A change to whether a person is asked.</summary>
    public static SessionEventType<ApprovalPolicyData> ApprovalPolicy { get; } =
        SessionEventRegistry.Register<ApprovalPolicyData>("approval/policy");

    /// <summary>The named preset a person selected, kept so their intent survives a reload.</summary>
    public static SessionEventType<PermissionPresetData> Preset { get; } =
        SessionEventRegistry.Register<PermissionPresetData>("permission/preset");

    /// <summary>An approval request, for audit.</summary>
    public static SessionEventType<ApprovalAskedData> Asked { get; } =
        SessionEventRegistry.Register<ApprovalAskedData>("approval/asked", surfaceEligible: false);

    /// <summary>An approval decision, for audit.</summary>
    public static SessionEventType<ApprovalDecidedData> Decided { get; } =
        SessionEventRegistry.Register<ApprovalDecidedData>("approval/decided", surfaceEligible: false);

    /// <summary>Force the permission vocabulary to be registered before a log is read.</summary>
    public static void EnsureRegistered()
    {
        _ = SandboxMode;
        _ = ApprovalPolicy;
        _ = Preset;
        _ = Asked;
        _ = Decided;
    }
}

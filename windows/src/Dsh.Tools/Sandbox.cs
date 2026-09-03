namespace Dsh.Tools;

/// <summary>
/// How much of the machine a session's tools may touch.
/// </summary>
/// <remarks>
/// Deliberately three coarse settings rather than a fine-grained rule language. A
/// person has to be able to answer "what can this agent do right now?" without
/// reading a policy file, and a coarse answer they understand is safer than a
/// precise one they do not.
/// </remarks>
public enum SandboxMode
{
    /// <summary>Read anything reachable; change nothing.</summary>
    ReadOnly,

    /// <summary>Read anything reachable; write only inside the session's workspace.</summary>
    WorkspaceWrite,

    /// <summary>No confinement at all.</summary>
    DangerFullAccess,
}

/// <summary>Whether a person is asked before a privileged action.</summary>
public enum ApprovalPolicy
{
    /// <summary>Put privileged actions to a person.</summary>
    Ask,

    /// <summary>Never ask; a privileged action is simply refused.</summary>
    Never,
}

/// <summary>
/// The session's confinement and approval settings.
/// </summary>
/// <param name="Sandbox">How much the tools may touch.</param>
/// <param name="Approval">Whether a person is asked before a privileged action.</param>
/// <param name="Workspace">The directory writes are confined to, when one is set.</param>
public sealed record SandboxState(
    SandboxMode Sandbox,
    ApprovalPolicy Approval,
    string? Workspace);

/// <summary>
/// The confinement capability's Service Definition, declared here because the tools
/// that consult it are the consumers.
/// </summary>
public interface ISandboxPolicy
{
    /// <summary>The settings currently in force.</summary>
    SandboxState State { get; }

    /// <summary>
    /// Whether a path may be written under the current settings.
    /// </summary>
    /// <param name="fullPath">An absolute path.</param>
    /// <returns>Null when the write is allowed, or the reason it is refused.</returns>
    string? RefuseWrite(string fullPath);

    /// <summary>
    /// Whether running a command needs a person's approval first.
    /// </summary>
    /// <returns>True when the current settings require asking.</returns>
    bool CommandNeedsApproval();
}

/// <summary>The context key the confinement capability is published under.</summary>
public static class SandboxKeys
{
    /// <summary>The context key a sandbox policy provider claims.</summary>
    public const string Service = "sandbox";
}

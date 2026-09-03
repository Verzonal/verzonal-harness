using Dsh.Agent;
using Dsh.Cordis;
using Dsh.Session;
using Dsh.Tools;

namespace Dsh.Interaction;

/// <summary>One named bundle of the two permission knobs.</summary>
/// <param name="Sandbox">How much the tools may touch.</param>
/// <param name="Approval">Whether a person is asked.</param>
/// <param name="Label">What a picker shows.</param>
/// <param name="Description">One sentence on what choosing it means.</param>
public sealed record PermissionPreset(
    SandboxMode Sandbox,
    ApprovalPolicy Approval,
    string Label,
    string Description);

/// <summary>How the permission capability is composed.</summary>
/// <param name="Presets">The named bundles a person can choose between.</param>
/// <param name="DefaultPreset">Which one a new session starts in.</param>
/// <param name="Workspace">The directory writes are confined to.</param>
public sealed record PermissionConfig(
    IReadOnlyDictionary<string, PermissionPreset> Presets,
    string DefaultPreset,
    string? Workspace)
{
    /// <summary>The name reserved for the state where the knobs match no preset.</summary>
    public const string Custom = "custom";

    /// <summary>
    /// The shipped table: read-only, workspace-write, and full access.
    /// </summary>
    /// <param name="workspace">The directory writes are confined to.</param>
    /// <returns>The default configuration.</returns>
    public static PermissionConfig Default(string? workspace) => new(
        new Dictionary<string, PermissionPreset>(StringComparer.Ordinal)
        {
            ["read-only"] = new(
                SandboxMode.ReadOnly,
                ApprovalPolicy.Ask,
                "Read only",
                "The agent can read and run read-only commands, and must ask before changing anything."),
            ["workspace-write"] = new(
                SandboxMode.WorkspaceWrite,
                ApprovalPolicy.Ask,
                "Workspace write",
                "The agent can change files inside the workspace, and must ask before reaching outside it."),
            ["danger-full-access"] = new(
                SandboxMode.DangerFullAccess,
                ApprovalPolicy.Never,
                "Full access",
                "The agent can do anything on this machine without asking."),
        },
        "workspace-write",
        workspace);

    /// <summary>
    /// Reject a table that could not serve its default.
    /// </summary>
    /// <exception cref="InvalidOperationException">The default names no preset, or the reserved name is used.</exception>
    public void Validate()
    {
        if (Presets.ContainsKey(Custom))
        {
            throw new InvalidOperationException(
                $"\"{Custom}\" is reserved for the derived not-a-preset state and cannot name a table entry");
        }

        if (!Presets.ContainsKey(DefaultPreset))
        {
            throw new InvalidOperationException(
                $"default permission preset \"{DefaultPreset}\" is not in the preset table");
        }
    }
}

/// <summary>
/// Owns the session's permission settings.
/// </summary>
/// <remarks>
/// Two independent knobs, with named presets over them. Presets are how a person
/// chooses; the knobs are what tools actually consult, and both are folded from the
/// log so a resumed session comes back exactly as it was left.
/// </remarks>
public sealed class PermissionService : Service, ISandboxPolicy
{
    private readonly PermissionConfig _config;
    private readonly Func<Session.Session?> _session;

    /// <param name="ctx">The mounting plugin's context.</param>
    /// <param name="config">The composed preset table and default.</param>
    /// <param name="session">Reads the session whose settings apply, when there is one.</param>
    public PermissionService(Context ctx, PermissionConfig config, Func<Session.Session?> session)
        : base(ctx, SandboxKeys.Service)
    {
        config.Validate();
        PermissionEvents.EnsureRegistered();
        _config = config;
        _session = session;
    }

    /// <summary>The named bundles a person can choose between.</summary>
    public IReadOnlyDictionary<string, PermissionPreset> Presets => _config.Presets;

    /// <inheritdoc />
    public SandboxState State
    {
        get
        {
            var fallback = _config.Presets[_config.DefaultPreset];
            var sandbox = fallback.Sandbox;
            var approval = fallback.Approval;

            foreach (var entry in _session()?.Events ?? [])
            {
                if (string.Equals(entry.Type, PermissionEvents.SandboxMode.Name, StringComparison.Ordinal))
                {
                    sandbox = entry.DataAs<SandboxModeData>().Mode;
                }
                else if (string.Equals(entry.Type, PermissionEvents.ApprovalPolicy.Name, StringComparison.Ordinal))
                {
                    approval = entry.DataAs<ApprovalPolicyData>().Policy;
                }
            }

            return new SandboxState(sandbox, approval, _config.Workspace);
        }
    }

    /// <summary>
    /// The preset the current knobs correspond to.
    /// </summary>
    /// <returns>
    /// The matching preset's name, or <see cref="PermissionConfig.Custom" /> when the
    /// knobs were moved individually and match no entry.
    /// </returns>
    public string CurrentPreset()
    {
        var state = State;
        foreach (var (name, preset) in _config.Presets)
        {
            if (preset.Sandbox == state.Sandbox && preset.Approval == state.Approval) return name;
        }

        return PermissionConfig.Custom;
    }

    /// <summary>
    /// Switch the session to a named preset.
    /// </summary>
    /// <param name="preset">The preset's name.</param>
    /// <exception cref="InvalidOperationException">No session is active, or the name is not in the table.</exception>
    public void SelectPreset(string preset)
    {
        if (!_config.Presets.TryGetValue(preset, out var selected))
        {
            throw new InvalidOperationException(
                $"unknown permission preset \"{preset}\"; available: {string.Join(", ", _config.Presets.Keys)}");
        }

        var session = _session() ?? throw new InvalidOperationException("no session is active");

        // The person's choice is recorded first, so their intent survives even when two
        // presets happen to share the same pair of knob values.
        session.Append(PermissionEvents.Preset, new PermissionPresetData(preset));

        var state = State;
        if (state.Sandbox != selected.Sandbox)
        {
            session.Append(PermissionEvents.SandboxMode, new SandboxModeData(selected.Sandbox));
        }

        if (state.Approval != selected.Approval)
        {
            session.Append(PermissionEvents.ApprovalPolicy, new ApprovalPolicyData(selected.Approval));
        }
    }

    /// <inheritdoc />
    public string? RefuseWrite(string fullPath)
    {
        var state = State;
        switch (state.Sandbox)
        {
            case SandboxMode.DangerFullAccess:
                return null;
            case SandboxMode.ReadOnly:
                return "the sandbox is read-only, so nothing on disk can be changed";
            default:
                break;
        }

        if (state.Workspace is null) return null;

        var workspace = Path.GetFullPath(state.Workspace);
        var target = Path.GetFullPath(fullPath);
        var inside = target.StartsWith(
            workspace.EndsWith(Path.DirectorySeparatorChar) ? workspace : workspace + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        return inside || string.Equals(target, workspace, StringComparison.Ordinal)
            ? null
            : $"writes are confined to the workspace, and {fullPath} is outside it";
    }

    /// <inheritdoc />
    public bool CommandNeedsApproval()
    {
        var state = State;
        return state.Sandbox != SandboxMode.DangerFullAccess && state.Approval == ApprovalPolicy.Ask;
    }

    /// <summary>Mount the permission capability over the initiating agent's session.</summary>
    /// <param name="config">The composed preset table and default.</param>
    /// <returns>The plugin to hand to <see cref="Context.Plugin" />.</returns>
    public static IPlugin Plugin(PermissionConfig config)
        => ServicePlugin.Create<PermissionService>(
            "permission-presets",
            SandboxKeys.Service,
            ctx => new PermissionService(ctx, config, () => AgentRegistry.CurrentInitiator?.Session));
}

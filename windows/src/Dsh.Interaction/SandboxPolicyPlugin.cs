using Dsh.Cordis;
using Dsh.Session;
using Dsh.Tools;

namespace Dsh.Interaction;

/// <summary>
/// Enforces the sandbox at the tool boundary, and turns a denial into a question a
/// person can answer.
/// </summary>
/// <remarks>
/// The escalation pair is the entire approval surface. Ordinary work never
/// interrupts anyone: a tool call that the sandbox refuses comes back as a refusal
/// the model can read, and the model may retry the same call once carrying
/// <c>sandbox_permissions</c> and a <c>justification</c>. Only that retry puts a
/// question to a person, and what they see is the model's own stated reason.
///
/// Enforcement lives here rather than inside each tool because a decision has to be
/// made where nothing can route around it. A tool that checked its own policy would
/// leave any other caller of the same capability unchecked.
/// </remarks>
public static class SandboxPolicyPlugin
{
    /// <summary>Tools whose calls write to disk, and the argument naming the target.</summary>
    private static readonly Dictionary<string, string> WritingTools = new(StringComparer.Ordinal)
    {
        ["write"] = "file_path",
        ["edit"] = "file_path",
    };

    /// <summary>Tools whose calls run a command.</summary>
    private static readonly HashSet<string> CommandTools = new(StringComparer.Ordinal) { "bash", "pwsh" };

    /// <summary>
    /// Mount the sandbox enforcement listener.
    /// </summary>
    /// <param name="resolvePath">Turns a model-written path into an absolute one.</param>
    /// <returns>The plugin to hand to <see cref="Context.Plugin" />.</returns>
    public static IPlugin Plugin(Func<string, string> resolvePath)
        => new FunctionPlugin(
            "sandbox-policy",
            ctx =>
            {
                ctx.OnWaterfall(ToolKeys.PreExecute, (payload, next) =>
                {
                    var policy = ctx.Get<ISandboxPolicy>(SandboxKeys.Service);
                    if (policy is null) return next();

                    var decision = Judge(policy, payload.Execution.Name, payload.Execution.Arguments, resolvePath);
                    return decision is null ? next() : Task.FromResult(decision);
                });

                return Task.CompletedTask;
            },
            ToolKeys.Service);

    /// <summary>
    /// Decide what to do about one call.
    /// </summary>
    /// <param name="policy">The settings in force.</param>
    /// <param name="toolName">The tool being called.</param>
    /// <param name="arguments">Its parsed arguments.</param>
    /// <param name="resolvePath">Turns a model-written path into an absolute one.</param>
    /// <returns>
    /// The decision this policy owns, or null to leave the call to the rest of the chain.
    /// </returns>
    internal static PreToolDecision? Judge(
        ISandboxPolicy policy,
        string toolName,
        JsonValue arguments,
        Func<string, string> resolvePath)
    {
        var args = arguments as JsonObject;
        var requested = (args?.Get("sandbox_permissions") as JsonString)?.Value;
        var justification = (args?.Get("justification") as JsonString)?.Value;

        if (requested is not null || justification is not null)
        {
            if (string.IsNullOrWhiteSpace(requested) || string.IsNullOrWhiteSpace(justification))
            {
                return new DenyDecision(
                    "sandbox_permissions and justification must be given together, and neither may be empty");
            }

            if (requested is not ("workspace-write" or "danger-full-access"))
            {
                return new DenyDecision(
                    $"\"{requested}\" is not a sandbox mode that can be escalated to");
            }

            return new AskDecision(justification);
        }

        if (WritingTools.TryGetValue(toolName, out var pathArgument))
        {
            var path = (args?.Get(pathArgument) as JsonString)?.Value;
            if (path is null) return null;

            var refusal = policy.RefuseWrite(resolvePath(path));
            return refusal is null
                ? null
                : new DenyDecision(
                    $"{refusal}. Retry the same call with sandbox_permissions and a justification if the user should be asked.");
        }

        if (CommandTools.Contains(toolName) && policy.State.Sandbox == SandboxMode.ReadOnly)
        {
            return new DenyDecision(
                "the sandbox is read-only, so commands are not run. Retry the same call with sandbox_permissions and a justification if the user should be asked.");
        }

        return null;
    }
}

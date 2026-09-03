using Dsh.Cordis;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Shell;
using Dsh.Tools;

namespace Dsh.Tools.Shell;

/// <summary>
/// Runs one command in the deployment's shell.
/// </summary>
/// <remarks>
/// Named for the shell it actually runs — <c>pwsh</c> on Windows, <c>bash</c>
/// elsewhere — because the model has to write commands in that shell's syntax, and a
/// neutral name would leave it guessing.
///
/// Each call gets a fresh shell, so <c>cd</c> does not persist between calls. That is
/// why the working directory is a parameter: a model relying on invisible carried
/// state would be wrong in ways nothing could see.
/// </remarks>
public sealed class ShellTool : ToolBase
{
    private readonly ShellService _shell;

    /// <param name="shell">The shell this tool runs through.</param>
    /// <param name="confined">Whether a confining backend adds the escalation properties.</param>
    public ShellTool(ShellService shell, bool confined = false)
    {
        _shell = shell;
        Name = shell.ShellName;

        Schema.Property[] properties =
        [
            new Schema.Property("command", Schema.String($"The {shell.ShellName} command to execute."), Required: true),
            new Schema.Property(
                "description",
                Schema.String(
                    "Clear, concise description of what this command does in active voice, 5-10 words (shown in the UI)."),
                Required: true),
            new Schema.Property(
                "timeoutMs",
                Schema.Number("Timeout in milliseconds. The executor applies its configured default and cap, and kills the command on expiry.")),
            new Schema.Property(
                "workdir",
                Schema.String("Working directory for this command. Defaults to the session workspace; a relative path is resolved against it.")),
            .. confined
                ?
                [
                    new Schema.Property(
                        "sandbox_permissions",
                        Schema.String(
                            "The wider sandbox mode this command needs. Only valid as a one-shot retry of an operation the sandbox just denied; requires justification and user approval.",
                            ["workspace-write", "danger-full-access"])),
                    new Schema.Property(
                        "justification",
                        Schema.String(
                            "Required with sandbox_permissions: one sentence for the user explaining why this exact command needs the wider access.")),
                ]
                : Array.Empty<Schema.Property>(),
        ];

        Parameters = Schema.Object(properties);
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override string Description =>
        $"Run a {Name} command. Each call starts a fresh shell, so use workdir rather than cd.";

    /// <inheritdoc />
    public override JsonSchemaNode Parameters { get; }

    /// <inheritdoc />
    public override ToolOutput Output { get; } = new(
        Schema.Object(
            new Schema.Property("output", Schema.String("Combined standard output and error."), Required: true),
            new Schema.Property("exitCode", Schema.Number("The command's exit status, when it exited on its own.")),
            new Schema.Property("timedOut", Schema.Boolean("Whether it was killed for running too long."), Required: true),
            new Schema.Property("truncated", Schema.Boolean("Whether the output shown is the tail of a longer stream."), Required: true)),
        Render,
        static (_, value) => value);

    /// <inheritdoc />
    public override int? TimeoutMs => 600_000;

    /// <inheritdoc />
    public override ToolCallView? PresentCall(JsonValue args)
    {
        var command = StringArg(args, "command");
        if (command is null) return null;
        return new TerminalCallView(command, StringArg(args, "description"), StringArg(args, "workdir"));
    }

    /// <inheritdoc />
    public override ToolResultView? PresentResult(JsonValue args, ToolResult result)
    {
        if (result.Meta is not JsonObject map) return null;
        var exit = (map.Get("exitCode") as JsonNumber)?.Value;
        return new TerminalResultView(
            (map.Get("output") as JsonString)?.Value ?? string.Empty,
            exit is null ? null : (int)exit.Value,
            (map.Get("timedOut") as JsonBool)?.Value == true ? "killed" : null);
    }

    /// <inheritdoc />
    public override async Task<JsonValue> ExecuteAsync(JsonValue args, ToolRunContext exec)
    {
        var result = await _shell.RunAsync(
            new ShellRequest(
                StringArg(args, "command")!,
                StringArg(args, "workdir"),
                NumberArg(args, "timeoutMs") is { } timeout ? (int)timeout : null),
            exec.CancellationToken);

        return JsonValue.From(new Dictionary<string, object?>
        {
            ["output"] = result.Output,
            ["exitCode"] = result.ExitCode,
            ["timedOut"] = result.TimedOut,
            ["truncated"] = result.Truncated,
        });
    }

    private static IReadOnlyList<ContentBlock> Render(JsonValue args, JsonValue value)
    {
        var map = (JsonObject)value;
        var output = (map.Get("output") as JsonString)?.Value ?? string.Empty;
        var builder = new System.Text.StringBuilder(output);

        if ((map.Get("timedOut") as JsonBool)?.Value == true)
        {
            if (builder.Length > 0) builder.AppendLine();
            builder.Append("[the command was killed for exceeding its timeout]");
        }
        else if ((map.Get("exitCode") as JsonNumber)?.Value is { } exit && exit != 0)
        {
            // A non-zero exit is the point of many commands, so it is stated plainly
            // rather than turned into a tool failure.
            if (builder.Length > 0) builder.AppendLine();
            builder.Append($"[exit code: {(int)exit}]");
        }

        if (builder.Length == 0) builder.Append("(no output)");
        return [new TextBlock(builder.ToString())];
    }
}

/// <summary>Mounts the shell tool.</summary>
public static class ShellTools
{
    /// <summary>
    /// Mount the shell tool under the mounted shell's own name.
    /// </summary>
    /// <returns>The plugin to hand to <see cref="Context.Plugin" />.</returns>
    public static IPlugin Plugin()
        => new FunctionPlugin(
            "tool-shell",
            ctx =>
            {
                var shell = ctx.Require<ShellService>(ShellKeys.Service);
                var confined = ctx.Get<ISandboxPolicy>(SandboxKeys.Service) is { } policy
                               && policy.State.Sandbox != SandboxMode.DangerFullAccess;

                ctx.Require<ToolRuntime>(ToolKeys.Service).Register(ctx, new ShellTool(shell, confined));
                return Task.CompletedTask;
            },
            ShellKeys.Service,
            ToolKeys.Service);
}

using Dsh.Agent;
using Dsh.Bundle.Base;
using Dsh.Cordis;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Llm.Fake;
using Dsh.Session;
using Dsh.Tools;

namespace Dsh.Cli;

/// <summary>
/// A console front-end over the harness.
/// </summary>
/// <remarks>
/// It exists to prove the assembled harness runs, and to give the desktop app a
/// second front-end to be checked against. Like the desktop app, it renders
/// <em>only</em> from session events — so whatever it shows is exactly what a replay
/// of the stored session would show.
/// </remarks>
public static class Program
{
    /// <summary>
    /// Run one task, or start an interactive session.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Zero when the run finished, one when it failed.</returns>
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        var options = ParseOptions(args);
        var prompt = string.Join(' ', args.Where(static argument => !argument.StartsWith('-')));

        ScriptedAdapter? scripted = null;
        if (options.Fake)
        {
            // Scripted to call a tool and then answer, so a run without an API key still
            // exercises the whole loop rather than just the streaming path.
            scripted = new ScriptedAdapter(
                ScriptedReply.ToolCalls(
                    [
                        new ToolCallBlock(
                            new CallId("call-1"),
                            "glob",
                            """{"pattern":"*"}"""),
                    ],
                    "Let me look at what is here."),
                ScriptedReply.Text(
                    "That is the workspace listing. This reply came from the scripted provider, so no API key was needed."));
        }

        await using var harness = await Harness.StartAsync(
            new HarnessOptions(
                options.Workspace,
                Provider: options.Fake ? ScriptedAdapter.ProviderRoute : "deepseek-official",
                Model: options.Fake ? ScriptedAdapter.ModelId : options.Model,
                Preset: options.Preset,
                ScriptedAdapter: scripted,
                Persist: !options.NoPersist),
            new ConsoleLogger(minimum: options.Verbose ? LogLevel.Debug : LogLevel.Warn));

        if (options.DumpComposition)
        {
            DumpComposition(harness);
            return 0;
        }

        AnswerApprovalsOnTheConsole(harness);

        await using var agent = await harness.CreateAgentAsync();
        Render(harness, agent.Agent);

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            agent.Agent.Cancel(UserCancel.Instance);
        };

        if (prompt.Length > 0)
        {
            await RunOneAsync(agent.Agent, prompt);
            return Failed(agent.Agent) ? 1 : 0;
        }

        await RunInteractiveAsync(agent.Agent);
        return 0;
    }

    private static async Task RunOneAsync(IAgent agent, string prompt)
    {
        agent.Followup(Message.UserText(prompt));
        await agent.WhenIdleAsync();
    }

    private static async Task RunInteractiveAsync(IAgent agent)
    {
        Console.WriteLine("Type a task, or an empty line to quit.");
        while (true)
        {
            Console.Write("\n> ");
            var line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) return;

            await RunOneAsync(agent, line);
        }
    }

    private static bool Failed(IAgent agent)
        => agent.Session.Events.LastOrDefault(static entry => entry.Type == SessionEvents.TurnEnd.Name)
               ?.DataAs<TurnEndData>().Reason is ErrorTurnEnd;

    /// <summary>
    /// Print the conversation as it happens, reading only from the session log.
    /// </summary>
    private static void Render(Harness harness, IAgent agent)
    {
        var streaming = false;

        harness.Ctx.On(SessionKeys.Event, notice =>
        {
            if (!ReferenceEquals(notice.Session, agent.Session)) return;
            var entry = notice.Event;

            switch (entry.Type)
            {
                case "assistant/chunk":
                    if (entry.DataAs<AssistantChunkData>().Chunk is TextDeltaChunk text)
                    {
                        if (!streaming)
                        {
                            Console.Write("\n");
                            streaming = true;
                        }

                        Console.Write(text.Text);
                    }

                    break;

                case "tool/call":
                {
                    var call = entry.DataAs<ToolCallData>();
                    streaming = false;
                    Console.Write($"\n  · {call.Name} {Trim(call.Arguments)}\n");
                    break;
                }

                case "tool/result":
                {
                    var result = entry.DataAs<ToolResultData>();
                    var block = (ToolResultBlock)result.Message.Content[0];
                    var body = ContentBlocks.FlattenText(block.Content);
                    Console.Write($"    {(block.IsError ? "failed" : "ok")}: {Trim(body)}\n");
                    break;
                }

                case "turn/end":
                {
                    streaming = false;
                    if (entry.DataAs<TurnEndData>().Reason is ErrorTurnEnd failure)
                    {
                        Console.Error.Write($"\n[{failure.Error.Code}] {failure.Error.Message}\n");
                    }
                    else
                    {
                        Console.Write("\n");
                    }

                    break;
                }

                default:
                    break;
            }
        });
    }

    /// <summary>
    /// Answer approval questions at the console.
    /// </summary>
    /// <remarks>
    /// A front-end that registers no answerer leaves every privileged action refused,
    /// which is the safe default — so this registration is what makes the console
    /// capable of granting anything at all.
    /// </remarks>
    private static void AnswerApprovalsOnTheConsole(Harness harness)
    {
        harness.Ctx.OnWaterfall(ApprovalEvents.Request, (question, next) =>
        {
            Console.Write($"\n{question.Request.ToolName} is asking for wider access.\n");
            if (question.Request.Reason is { } reason) Console.Write($"  reason: {reason}\n");
            Console.Write("  allow once? [y/N] ");

            var answer = Console.ReadLine();
            var allowed = answer is not null
                          && (answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase)
                              || answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase));

            return Task.FromResult(allowed ? ApprovalOutcome.AllowedOnce : ApprovalOutcome.Rejected);
        });
    }

    private static void DumpComposition(Harness harness)
    {
        Console.WriteLine($"workspace: {Path.GetFullPath(harness.Options.Workspace)}");
        Console.WriteLine($"route:     {harness.Options.Provider}/{harness.Options.Model}");
        Console.WriteLine($"preset:    {harness.Permissions.CurrentPreset()}");
        Console.WriteLine();
        Console.WriteLine("plugins:");
        foreach (var row in harness.Rows)
        {
            var waiting = row.Inject.Count == 0 ? string.Empty : $"  needs {string.Join(", ", row.Inject)}";
            var failure = row.Error is null ? string.Empty : $"  ({row.Error})";
            Console.WriteLine($"  {row.State,-9} {row.Name}{waiting}{failure}");
        }

        Console.WriteLine();
        Console.WriteLine("tools:");
        foreach (var schema in harness.Tools.Schemas(null))
        {
            Console.WriteLine($"  {schema.Name,-12} {schema.Description}");
        }
    }

    private static string Trim(string text)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= 120 ? single : single[..120] + "…";
    }

    private sealed record Options(
        string Workspace,
        string Model,
        string Preset,
        bool Fake,
        bool NoPersist,
        bool Verbose,
        bool DumpComposition);

    private static Options ParseOptions(string[] args)
    {
        string? Value(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        return new Options(
            Value("--workspace") ?? Directory.GetCurrentDirectory(),
            Value("--model") ?? Dsh.Llm.DeepSeek.DeepSeekConfig.DefaultModel,
            Value("--preset") ?? "workspace-write",
            args.Contains("--fake"),
            args.Contains("--no-persist"),
            args.Contains("--verbose"),
            args.Contains("--dump-composition"));
    }

    private static void PrintUsage() => Console.WriteLine(
        """
        dsh — DeepSeek Harness console front-end

          dsh "task"                 run one task and exit
          dsh                        start an interactive session

        Options:
          --workspace <path>         where the agent works (default: current directory)
          --model <id>               the model to use
          --preset <name>            read-only | workspace-write | danger-full-access
          --fake                     use the scripted provider, so no API key is needed
          --no-persist               keep the session in memory only
          --dump-composition         print the mounted plugins and tools, then exit
          --verbose                  report contained failures
        """);
}

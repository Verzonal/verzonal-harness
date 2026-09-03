using Dsh.Agent;
using Dsh.AgentLoop;
using Dsh.Cordis;
using Dsh.Credentials;
using Dsh.Fs;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Llm.DeepSeek;
using Dsh.Llm.Fake;
using Dsh.Session;
using Dsh.Session.Persistence;
using Dsh.Settings;
using Dsh.Shell;
using Dsh.SystemPrompt;
using Dsh.Tools;
using Dsh.Tools.Fs;
using Dsh.Tools.Shell;
using Dsh.Tools.Todo;
using Dsh.Util;

namespace Dsh.Bundle.Base;

/// <summary>How a harness instance is composed.</summary>
/// <param name="Workspace">The directory the agent works in.</param>
/// <param name="HarnessHome">Where user data lives; the default home when omitted.</param>
/// <param name="Provider">The model route to use.</param>
/// <param name="Model">The model to use.</param>
/// <param name="Preset">Which permission preset a new session starts in.</param>
/// <param name="ScriptedAdapter">
/// A scripted provider to mount instead of the real one, so the whole harness can run
/// with no API key.
/// </param>
/// <param name="Persist">Whether sessions are written to disk.</param>
/// <param name="MaxParallelToolCalls">How many tool calls may overlap in one step.</param>
public sealed record HarnessOptions(
    string Workspace,
    string? HarnessHome = null,
    string Provider = "deepseek-official",
    string Model = DeepSeekConfig.DefaultModel,
    string Preset = "workspace-write",
    ScriptedAdapter? ScriptedAdapter = null,
    bool Persist = true,
    int MaxParallelToolCalls = 10);

/// <summary>One mounted plugin, as a settings page would list it.</summary>
/// <param name="Name">The plugin's name.</param>
/// <param name="State">Whether it is active, waiting on a service, or failed.</param>
/// <param name="Inject">The services it waits for.</param>
/// <param name="Error">Why it failed, when it did.</param>
public sealed record CompositionRow(string Name, FiberState State, IReadOnlyList<string> Inject, string? Error)
{
    /// <summary>The state as text, for a listing that shows it beside the name.</summary>
    public string StateLabel => State.ToString();

    /// <summary>What the row waits for, as one line, or empty when it waits for nothing.</summary>
    public string InjectLabel => Inject.Count == 0 ? string.Empty : $"needs {string.Join(", ", Inject)}";
}

/// <summary>
/// A running harness: the composition, plus the handle to drive it.
/// </summary>
/// <remarks>
/// This is the whole product in one place — the same set of plugins whichever
/// front-end boots it. Nothing here is privileged: every capability is a row that a
/// different composition could replace, which is what
/// <see cref="Rows" /> exists to make visible to a person rather than only to a
/// reader of the source.
/// </remarks>
public sealed class Harness : IAsyncDisposable
{
    private readonly List<Fiber> _fibers = [];
    private readonly List<AgentHandle> _agents = [];
    private readonly List<IDisposable> _sessionAttachments = [];

    private Harness(Context ctx, HarnessOptions options)
    {
        Ctx = ctx;
        Options = options;
    }

    /// <summary>The root context every capability is mounted on.</summary>
    public Context Ctx { get; }

    /// <summary>How this instance was composed.</summary>
    public HarnessOptions Options { get; }

    /// <summary>The tool registry.</summary>
    public ToolRuntime Tools => Ctx.Require<ToolRuntime>(ToolKeys.Service);

    /// <summary>The prompt assembly.</summary>
    public SystemPromptService Prompt => Ctx.Require<SystemPromptService>(SystemPromptKeys.Service);

    /// <summary>The live session registry.</summary>
    public SessionStore Sessions => Ctx.Require<SessionStore>(SessionKeys.Service);

    /// <summary>The permission settings.</summary>
    public PermissionService Permissions => Ctx.Require<PermissionService>(SandboxKeys.Service);

    /// <summary>The user's settings document.</summary>
    public SettingsService Settings => Ctx.Require<SettingsService>(SettingsKeys.Service);

    /// <summary>The credential store.</summary>
    public CredentialProvider Credentials => Ctx.Require<CredentialProvider>(CredentialKeys.Service);

    /// <summary>The durable session store, when one is mounted.</summary>
    public JsonlPersistence? Persistence => Ctx.Get<JsonlPersistence>(PersistenceKeys.Service);

    /// <summary>Every mounted plugin and its state.</summary>
    public IReadOnlyList<CompositionRow> Rows =>
    [
        .. _fibers.Select(static fiber => new CompositionRow(
            fiber.Name,
            fiber.State,
            fiber.Inject,
            fiber.Error?.Message)),
    ];

    /// <summary>
    /// Boot a harness.
    /// </summary>
    /// <param name="options">How to compose it.</param>
    /// <param name="logger">Where contained failures are reported.</param>
    /// <returns>The running harness.</returns>
    public static async Task<Harness> StartAsync(HarnessOptions options, ILogger? logger = null)
    {
        var workspace = Path.GetFullPath(options.Workspace);
        var home = HomePaths.Resolve(options.HarnessHome);
        var ctx = Context.CreateRoot(logger);
        var harness = new Harness(ctx, options);

        // Order below is for a reader's benefit only: each plugin activates when the
        // services it injects exist, so the composition is a set, not a sequence.
        await harness.MountAsync(SettingsService.Plugin(Path.Combine(home, "settings.yaml")));
        await harness.MountAsync(LocalCredentials.Plugin(
            Path.Combine(home, LocalCredentials.FileName),
            workspace));

        await harness.MountAsync(LlmRuntime.Plugin());
        await harness.MountAsync(SessionStore.Plugin());
        await harness.MountAsync(ToolRuntime.Plugin());
        await harness.MountAsync(SystemPromptService.Plugin());
        await harness.MountAsync(AgentRegistry.Plugin());

        if (options.Persist) await harness.MountAsync(JsonlPersistence.Plugin(Path.Combine(home, "sessions")));

        await harness.MountAsync(LocalFileSystem.Plugin(workspace));
        await harness.MountAsync(LocalShell.Plugin(new LocalShellConfig(workspace)));

        var permissions = PermissionConfig.Default(workspace) with
        {
            DefaultPreset = harness.ResolveDefaultPreset(options.Preset),
        };
        await harness.MountAsync(PermissionService.Plugin(permissions));
        await harness.MountAsync(ApprovalService.Plugin());
        await harness.MountAsync(SandboxPolicyPlugin.Plugin(
            path => ctx.Require<FileSystemService>(FsKeys.Service).Resolve(path)));

        await harness.MountAsync(FsTools.Plugin());
        await harness.MountAsync(ShellTools.Plugin());
        await harness.MountAsync(TodoTools.Plugin());

        if (options.ScriptedAdapter is { } scripted)
        {
            await harness.MountAsync(ScriptedAdapter.Plugin(scripted, options.Provider));
        }
        else
        {
            await harness.MountAsync(DeepSeekAdapter.Plugin(new DeepSeekConfig(options.Provider)));
        }

        await harness.MountAsync(AgentLoopService.Plugin(new AgentLoopConfig(options.MaxParallelToolCalls)));

        await harness.MountAsync(WorkspacePrompt.Plugin(workspace));

        // The registry feeds prompt assembly, so the model's tool list is always the
        // set actually registered rather than a second list that could drift from it.
        harness.Prompt.ToolProvider(ctx, assemble => new ToolProviderResult(harness.Tools.Schemas(assemble.Scope)));

        return harness;
    }

    private string ResolveDefaultPreset(string requested)
    {
        // A saved preference is what a person set last time; the composed value is only
        // the fallback for someone who has never chosen.
        var settings = Ctx.Get<SettingsService>(SettingsKeys.Service);
        return settings?.Get("permission", "defaultPreset", requested) ?? requested;
    }

    private async Task MountAsync(IPlugin plugin)
    {
        var fiber = Ctx.Plugin(plugin);
        await fiber.WhenSettledAsync();
        _fibers.Add(fiber);
    }

    /// <summary>
    /// Open an agent over a new session.
    /// </summary>
    /// <param name="cancellationToken">Cancels creation.</param>
    /// <returns>The agent and its teardown.</returns>
    public Task<AgentHandle> CreateAgentAsync(CancellationToken cancellationToken = default)
    {
        var (session, detach) = Sessions.Create(
            SessionStore.NewHeader(Sessions.MintId(), Path.GetFullPath(Options.Workspace)));

        // Held so teardown detaches the session, which is what lets persistence write
        // out anything still queued when the process is closing.
        _sessionAttachments.Add(detach);
        return OpenAsync(session, SessionStartSource.Startup, cancellationToken);
    }

    /// <summary>
    /// Open an agent over a stored session.
    /// </summary>
    /// <param name="logPath">The stored log to reopen.</param>
    /// <param name="cancellationToken">Cancels creation.</param>
    /// <returns>The agent and its teardown.</returns>
    public Task<AgentHandle> ResumeAgentAsync(string logPath, CancellationToken cancellationToken = default)
    {
        var restored = JsonlPersistence.Resume(logPath);
        var prepared = Sessions.Prepare(restored.Header, restored.Events);
        return OpenAsync(prepared.Session, SessionStartSource.Resume, cancellationToken);
    }

    private async Task<AgentHandle> OpenAsync(
        Dsh.Session.Session session,
        SessionStartSource source,
        CancellationToken cancellationToken)
    {
        var handle = await Ctx.Require<AgentRegistry>(AgentKeys.Service).CreateAsync(
            Ctx,
            session,
            new AgentOptions(Options.Provider, Options.Model),
            source,
            cancellationToken);

        _agents.Add(handle);
        return handle;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var agent in _agents) await agent.DisposeAsync();
        for (var index = _sessionAttachments.Count - 1; index >= 0; index--) _sessionAttachments[index].Dispose();
        for (var index = _fibers.Count - 1; index >= 0; index--) await _fibers[index].DisposeAsync();
    }
}

/// <summary>Tells the model where it is working and what it is.</summary>
internal static class WorkspacePrompt
{
    public static IPlugin Plugin(string workspace)
        => new FunctionPlugin(
            "agent-instructions",
            ctx =>
            {
                var prompt = ctx.Require<SystemPromptService>(SystemPromptKeys.Service);

                prompt.Variable(ctx, "workspace", _ => workspace);
                prompt.Section(ctx, PromptSection.Fixed(
                    SystemPromptService.PersonaSection,
                    0,
                    """
                    You are a coding agent working in a user's project.

                    Use the tools to read and change files and to run commands rather than
                    guessing at what a file contains. Prefer small, verifiable steps, and
                    say plainly when something did not work.

                    The workspace is {{workspace}}. Relative paths resolve against it.
                    """));

                prompt.ContextSection(ctx, new PromptContextSection(
                    "workspace",
                    0,
                    _ => $"Workspace: {workspace}"));

                return Task.CompletedTask;
            },
            SystemPromptKeys.Service);
}

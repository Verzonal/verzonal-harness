using Dsh.Agent;
using Dsh.AgentLoop;
using Dsh.Cordis;
using Dsh.Llm;
using Dsh.Llm.Fake;
using Dsh.Session;
using Dsh.SystemPrompt;
using Dsh.Tools;

namespace Dsh.Tests.AgentLoop;

/// <summary>
/// Boots the real composition — registry, log, tools, prompt, driver — with only the
/// model's answers scripted, so a test exercises the assembled harness rather than a
/// hand-built stand-in.
/// </summary>
internal sealed class LoopFixture : IAsyncDisposable
{
    private readonly List<Fiber> _fibers = [];
    private AgentHandle? _handle;

    private LoopFixture(Context ctx, ScriptedAdapter adapter)
    {
        Ctx = ctx;
        Adapter = adapter;
    }

    public Context Ctx { get; }

    public ScriptedAdapter Adapter { get; }

    public ToolRuntime Tools => Ctx.Require<ToolRuntime>(ToolKeys.Service);

    public SystemPromptService Prompt => Ctx.Require<SystemPromptService>(SystemPromptKeys.Service);

    public SessionStore Sessions => Ctx.Require<SessionStore>(SessionKeys.Service);

    public IAgent Agent => _handle!.Agent;

    public Dsh.Session.Session Session => Agent.Session;

    public static async Task<LoopFixture> StartAsync(
        ScriptedAdapter adapter,
        AgentLoopConfig? config = null,
        Action<Context>? configure = null)
    {
        var ctx = Context.CreateRoot();
        var fixture = new LoopFixture(ctx, adapter);

        await fixture.MountAsync(LlmRuntime.Plugin());
        await fixture.MountAsync(SessionStore.Plugin());
        await fixture.MountAsync(ToolRuntime.Plugin());
        await fixture.MountAsync(SystemPromptService.Plugin(includeHarnessIdentity: false));
        await fixture.MountAsync(AgentRegistry.Plugin());
        await fixture.MountAsync(ScriptedAdapter.Plugin(adapter));
        await fixture.MountAsync(AgentLoopService.Plugin(config));

        // The tool registry feeds the assembly, exactly as the shipped composition does.
        fixture.Prompt.ToolProvider(ctx, assemble => new ToolProviderResult(fixture.Tools.Schemas(assemble.Scope)));

        configure?.Invoke(ctx);

        var (session, _) = fixture.Sessions.Create(
            SessionStore.NewHeader(fixture.Sessions.MintId(), "/workspace"));

        fixture._handle = await ctx.Require<AgentRegistry>(AgentKeys.Service).CreateAsync(
            ctx,
            session,
            new AgentOptions(ScriptedAdapter.ProviderRoute, ScriptedAdapter.ModelId));

        return fixture;
    }

    private async Task MountAsync(IPlugin plugin)
    {
        var fiber = Ctx.Plugin(plugin);
        await fiber.WhenSettledAsync();
        _fibers.Add(fiber);
    }

    /// <summary>Send a prompt and wait for the turn it opens to finish.</summary>
    public async Task PromptAsync(string text)
    {
        Agent.Followup(Message.UserText(text));
        await Agent.WhenIdleAsync();
    }

    /// <summary>The log's event types, in order, for asserting the shape of a turn.</summary>
    public IReadOnlyList<string> EventTypes(params string[] ignoring)
        => [.. Session.Events
            .Select(static entry => entry.Type)
            .Where(type => !ignoring.Contains(type, StringComparer.Ordinal))];

    /// <summary>The reason recorded on the most recent turn.</summary>
    public TurnEndReason LastTurnEnd()
        => Session.Events.Last(static entry => entry.Type == SessionEvents.TurnEnd.Name)
            .DataAs<TurnEndData>().Reason;

    public async ValueTask DisposeAsync()
    {
        if (_handle is not null) await _handle.DisposeAsync();
        for (var index = _fibers.Count - 1; index >= 0; index--) await _fibers[index].DisposeAsync();
    }
}

/// <summary>A tool whose behavior a test supplies inline.</summary>
internal sealed class ProbeTool : ToolBase
{
    private readonly Func<JsonValue, ToolRunContext, Task<JsonValue>> _body;

    public ProbeTool(
        string name,
        Func<JsonValue, ToolRunContext, Task<JsonValue>> body,
        bool concurrencySafe = false)
    {
        Name = name;
        _body = body;
        ConcurrencySafe = concurrencySafe;
    }

    public override string Name { get; }

    public override string Description => $"Probe tool {Name}.";

    public override JsonSchemaNode Parameters { get; } = Schema.Object(
        new Schema.Property("value", Schema.String("Anything the test wants to pass.")));

    public override ToolOutput Output { get; } = new(
        Schema.Object(new Schema.Property("text", Schema.String("What the tool produced."), Required: true)),
        (_, value) => [new TextBlock(((value as JsonObject)?.Get("text") as JsonString)?.Value ?? string.Empty)]);

    public bool ConcurrencySafe { get; }

    public override bool IsConcurrencySafe(JsonValue args) => ConcurrencySafe;

    public override Task<JsonValue> ExecuteAsync(JsonValue args, ToolRunContext exec) => _body(args, exec);

    public static JsonValue Text(string text)
        => JsonValue.From(new Dictionary<string, object?> { ["text"] = text });
}

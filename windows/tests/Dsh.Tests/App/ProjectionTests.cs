using Dsh.Agent;
using Dsh.App.Core;
using Dsh.Llm;
using Dsh.Llm.Fake;
using Dsh.Session;
using Dsh.Tests.AgentLoop;
using Dsh.Tools;

namespace Dsh.Tests.App;

public sealed class ProjectionTests
{
    private static ToolCallBlock Call(string id, string name, string arguments = "{}")
        => new(new CallId(id), name, arguments);

    private static ConversationProjection Project(LoopFixture fixture)
    {
        var projection = new ConversationProjection(
            name => fixture.Tools.View(fixture.Agent.Scope).GetValueOrDefault(name));
        projection.Replay(fixture.Session.Events);
        return projection;
    }

    [Fact]
    public async Task A_prompt_and_its_answer_become_two_rows()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(ScriptedReply.Text("hello back")));
        await fixture.PromptAsync("hello");

        var projection = Project(fixture);

        Assert.Collection(
            projection.Nodes,
            static node => Assert.Equal("hello", Assert.IsType<UserNode>(node).Text),
            static node => Assert.Equal("hello back", Assert.IsType<AssistantNode>(node).Text));
    }

    [Fact]
    public async Task A_tool_result_belongs_to_its_tool_row_not_a_user_bubble()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(
            ScriptedReply.ToolCalls([Call("call-1", "probe")]),
            ScriptedReply.Text("done")));
        fixture.Tools.Register(fixture.Ctx, new ProbeTool("probe", (_, _) => Task.FromResult(ProbeTool.Text("output"))));
        await fixture.PromptAsync("use it");

        var projection = Project(fixture);

        Assert.Single(projection.Nodes.OfType<UserNode>());
        var tool = Assert.Single(projection.Nodes.OfType<ToolNode>());
        Assert.Equal(ToolNodeState.Completed, tool.State);
        Assert.Equal("output", tool.ResultText);
    }

    [Fact]
    public async Task A_failed_call_marks_its_row_failed()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(
            ScriptedReply.ToolCalls([Call("call-1", "nope")]),
            ScriptedReply.Text("sorry")));
        await fixture.PromptAsync("try it");

        var tool = Assert.Single(Project(fixture).Nodes.OfType<ToolNode>());

        Assert.Equal(ToolNodeState.Failed, tool.State);
    }

    [Fact]
    public async Task A_tool_that_declares_a_terminal_card_gets_one()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(
            ScriptedReply.ToolCalls([Call("call-1", "runner", """{"value":"ls -la"}""")]),
            ScriptedReply.Text("done")));
        fixture.Tools.Register(fixture.Ctx, new TerminalProbe());
        await fixture.PromptAsync("run it");

        var tool = Assert.Single(Project(fixture).Nodes.OfType<ToolNode>());

        Assert.True(tool.IsTerminal);
        Assert.Equal("ls -la", tool.Title);
        var view = Assert.IsType<TerminalResultView>(tool.ResultView);
        Assert.Equal(0, view.ExitCode);
    }

    private sealed class TerminalProbe : ToolBase
    {
        public override string Name => "runner";

        public override string Description => "Runs something.";

        public override JsonSchemaNode Parameters { get; } = Schema.Object(
            new Schema.Property("value", Schema.String("The command.")));

        public override ToolOutput Output { get; } = new(
            Schema.Object(new Schema.Property("text", Schema.String("Its output."), Required: true)),
            static (_, value) => [new TextBlock(((value as JsonObject)?.Get("text") as JsonString)?.Value ?? string.Empty)],
            static (_, value) => value);

        public override ToolCallView? PresentCall(JsonValue args)
            => new TerminalCallView(StringArg(args, "value") ?? "command", "Runs a command");

        public override ToolResultView? PresentResult(JsonValue args, ToolResult result)
            => new TerminalResultView(
                ((result.Meta as JsonObject)?.Get("text") as JsonString)?.Value ?? string.Empty,
                0);

        public override Task<JsonValue> ExecuteAsync(JsonValue args, ToolRunContext exec)
            => Task.FromResult(JsonValue.From(new Dictionary<string, object?> { ["text"] = "total 0" }));
    }

    [Fact]
    public async Task A_presenter_that_throws_costs_its_card_not_the_conversation()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(
            ScriptedReply.ToolCalls([Call("call-1", "rude")]),
            ScriptedReply.Text("done")));
        fixture.Tools.Register(fixture.Ctx, new RudePresenter());
        await fixture.PromptAsync("run it");

        var tool = Assert.Single(Project(fixture).Nodes.OfType<ToolNode>());

        Assert.Null(tool.CallView);
        Assert.Equal("rude", tool.Title);
        Assert.Equal(ToolNodeState.Completed, tool.State);
    }

    private sealed class RudePresenter : ToolBase
    {
        public override string Name => "rude";

        public override string Description => "Has a broken presenter.";

        public override JsonSchemaNode Parameters { get; } = Schema.EmptyObject();

        public override ToolOutput Output { get; } = new(
            Schema.Object(new Schema.Property("text", Schema.String("Output."), Required: true)),
            static (_, _) => [new TextBlock("fine")]);

        public override ToolCallView? PresentCall(JsonValue args) => throw new InvalidOperationException("broken");

        public override ToolResultView? PresentResult(JsonValue args, ToolResult result)
            => throw new InvalidOperationException("broken");

        public override Task<JsonValue> ExecuteAsync(JsonValue args, ToolRunContext exec)
            => Task.FromResult(JsonValue.From(new Dictionary<string, object?> { ["text"] = "ok" }));
    }

    [Fact]
    public async Task Injected_context_is_marked_as_context_rather_than_shown_as_a_prompt()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(ScriptedReply.Text("noted")));
        fixture.Agent.Inject(Message.Context(
            "watcher",
            ContextForm.Notice,
            [new TextBlock("a file changed")]));
        await fixture.PromptAsync("carry on");

        var context = Project(fixture).Nodes.OfType<UserNode>().Single(static node => node.IsContext);

        Assert.Equal("watcher", context.Producer);
        Assert.Equal(ContextForm.Notice, context.Form);
        Assert.Contains("watcher", context.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_turn_shows_the_providers_own_failure()
    {
        await using var fixture = await LoopFixture.StartAsync(
            new ScriptedAdapter(ScriptedReply.Failure("the provider is down", LlmErrorCodes.Server)));
        await fixture.PromptAsync("hello");

        var failure = Assert.Single(Project(fixture).Nodes.OfType<TurnFailureNode>());

        Assert.Equal("the provider is down", failure.Message);
        Assert.Equal(LlmErrorCodes.Server, failure.Code);
        Assert.False(failure.WasCancelled);
    }

    [Fact]
    public async Task Each_step_gets_its_own_assistant_row()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(
            ScriptedReply.ToolCalls([Call("call-1", "probe")], "Working on it."),
            ScriptedReply.Text("All done.")));
        fixture.Tools.Register(fixture.Ctx, new ProbeTool("probe", (_, _) => Task.FromResult(ProbeTool.Text("ok"))));
        await fixture.PromptAsync("go");

        var assistants = Project(fixture).Nodes.OfType<AssistantNode>().ToList();

        Assert.Equal(2, assistants.Count);
        Assert.Equal("Working on it.", assistants[0].Text);
        Assert.Equal("All done.", assistants[1].Text);
    }

    [Fact]
    public async Task Thinking_is_kept_separate_from_the_answer()
    {
        await using var fixture = await LoopFixture.StartAsync(
            new ScriptedAdapter(ScriptedReply.Reasoned("weighing the options", "the answer")));
        await fixture.PromptAsync("think about it");

        var assistant = Assert.Single(Project(fixture).Nodes.OfType<AssistantNode>());

        Assert.True(assistant.HasReasoning);
        Assert.Equal("weighing the options", assistant.Reasoning);
        Assert.Equal("the answer", assistant.Text);
        Assert.Equal("weighing the options", assistant.ReasoningSummary);
    }

    [Fact]
    public async Task Streaming_live_and_replaying_afterwards_produce_the_same_rows()
    {
        var adapter = new ScriptedAdapter(
            ScriptedReply.ToolCalls([Call("call-1", "probe")], "Let me check."),
            ScriptedReply.Text("Here is the answer."))
        {
            ChunkDelay = TimeSpan.FromMilliseconds(1),
        };

        await using var fixture = await LoopFixture.StartAsync(adapter);
        fixture.Tools.Register(fixture.Ctx, new ProbeTool("probe", (_, _) => Task.FromResult(ProbeTool.Text("checked"))));

        // Fed event by event, exactly as the live app receives them.
        var live = new ConversationProjection(
            name => fixture.Tools.View(fixture.Agent.Scope).GetValueOrDefault(name));
        using var subscription = fixture.Session.OnEvent(live.Apply);

        await fixture.PromptAsync("check something");

        var replayed = Project(fixture);

        Assert.Equal(Describe(replayed), Describe(live));
    }

    [Fact]
    public async Task A_checklist_write_updates_the_projection()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(ScriptedReply.Text("done")));
        await fixture.PromptAsync("hello");
        fixture.Session.Append(SessionEvents.TodoWrite, new TodoWriteData(
        [
            new TodoItem("first", TodoStatus.Completed),
            new TodoItem("second", TodoStatus.InProgress),
        ]));

        var projection = Project(fixture);

        Assert.Equal(2, projection.Todos.Count);
        Assert.Equal(TodoStatus.Completed, projection.Todos[0].Status);
    }

    [Fact]
    public async Task Context_pressure_is_reported_once_the_window_and_usage_are_known()
    {
        await using var fixture = await LoopFixture.StartAsync(
            new ScriptedAdapter(ScriptedReply.Text("hi", new TokenUsage(20_000, 500))));
        await fixture.PromptAsync("hello");

        var projection = Project(fixture);

        Assert.Equal(200_000, projection.ContextWindow);
        Assert.NotNull(projection.ContextPressure);
        Assert.InRange(projection.ContextPressure!.Value, 0.10, 0.11);
    }

    private static IReadOnlyList<string> Describe(ConversationProjection projection)
        => [.. projection.Nodes.Select(static node => node switch
        {
            UserNode user => $"user:{user.Text}",
            AssistantNode assistant => $"assistant:{assistant.Text}|{assistant.Reasoning}",
            ToolNode tool => $"tool:{tool.ToolName}:{tool.State}:{tool.ResultText}",
            TurnFailureNode failure => $"failure:{failure.Code}",
            _ => "?",
        })];
}

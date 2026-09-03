using Dsh.Cordis;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Tools;

namespace Dsh.Tests.Tools;

public sealed class ToolRuntimeTests
{
    private sealed class EchoTool : ToolBase
    {
        private readonly Func<JsonValue, ToolRunContext, Task<JsonValue>>? _body;

        public EchoTool(string name = "echo", Func<JsonValue, ToolRunContext, Task<JsonValue>>? body = null)
        {
            Name = name;
            _body = body;
        }

        public override string Name { get; }

        public override string Description => "Echo the text back.";

        public override JsonSchemaNode Parameters => Schema.Object(
            new Schema.Property("text", Schema.String("The text to echo."), Required: true));

        public override ToolOutput Output { get; } = new(
            Schema.Object(new Schema.Property("echoed", Schema.String("What was echoed."), Required: true)),
            (_, value) => [new TextBlock(((value as JsonObject)?.Get("echoed") as JsonString)?.Value ?? string.Empty)]);

        public bool Concurrent { get; init; }

        public override bool IsConcurrencySafe(JsonValue args) => Concurrent;

        public override Task<JsonValue> ExecuteAsync(JsonValue args, ToolRunContext exec)
        {
            if (_body is not null) return _body(args, exec);
            var text = StringArg(args, "text") ?? string.Empty;
            return Task.FromResult(JsonValue.From(new Dictionary<string, object?> { ["echoed"] = text }));
        }
    }

    private static async Task<(Context Ctx, ToolRuntime Tools)> RuntimeAsync()
    {
        var ctx = Context.CreateRoot();
        var fiber = ctx.Plugin(ToolRuntime.Plugin());
        await fiber.WhenSettledAsync();
        return (ctx, ctx.Require<ToolRuntime>(ToolKeys.Service));
    }

    private static JsonValue Args(string text)
        => JsonValue.From(new Dictionary<string, object?> { ["text"] = text });

    private static ToolExecutionInput Call(string name = "echo", JsonValue? args = null, ScopeKey? scope = null)
        => new(new CallId("call-1"), name, args ?? Args("hi"), scope);

    [Fact]
    public async Task A_registered_tool_becomes_visible_and_callable()
    {
        var (ctx, tools) = await RuntimeAsync();
        tools.Register(ctx, new EchoTool());

        var result = await tools.ExecuteAsync(Call(), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("hi", Assert.IsType<TextBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task Schemas_expose_only_what_the_model_may_see()
    {
        var (ctx, tools) = await RuntimeAsync();
        tools.Register(ctx, new EchoTool());

        var schema = Assert.Single(tools.Schemas(null));

        Assert.Equal("echo", schema.Name);
        Assert.Equal("Echo the text back.", schema.Description);
        Assert.Equal("object", schema.Parameters["type"]);
    }

    [Fact]
    public async Task Registering_the_same_name_twice_in_one_layer_is_refused()
    {
        var (ctx, tools) = await RuntimeAsync();
        tools.Register(ctx, new EchoTool());

        Assert.Throws<InvalidOperationException>(() => tools.Register(ctx, new EchoTool()));
    }

    [Fact]
    public async Task Withdrawing_a_registration_removes_the_tool()
    {
        var (ctx, tools) = await RuntimeAsync();
        var registration = tools.Register(ctx, new EchoTool());

        Assert.Single(tools.Schemas(null));
        registration.Dispose();
        Assert.Empty(tools.Schemas(null));
    }

    [Fact]
    public async Task A_restriction_hides_an_inherited_tool_from_its_scope()
    {
        var (ctx, tools) = await RuntimeAsync();
        tools.Register(ctx, new EchoTool());
        var scope = new ScopeKey("agent");
        tools.Restrict(ctx.WithScope(scope), new ToolRestriction(Deny: ["echo"]));

        Assert.Single(tools.Schemas(null));
        Assert.Empty(tools.Schemas(scope));
    }

    [Fact]
    public async Task Restrictions_intersect_so_a_later_one_can_only_narrow()
    {
        var (ctx, tools) = await RuntimeAsync();
        tools.Register(ctx, new EchoTool("alpha"));
        tools.Register(ctx, new EchoTool("beta"));
        var scope = new ScopeKey("agent");
        var scoped = ctx.WithScope(scope);

        tools.Restrict(scoped, new ToolRestriction(Allow: ["alpha", "beta"]));
        tools.Restrict(scoped, new ToolRestriction(Allow: ["alpha"]));

        Assert.Equal(["alpha"], tools.Schemas(scope).Select(static schema => schema.Name));
    }

    [Fact]
    public async Task A_scopes_own_registration_survives_a_restriction_that_would_hide_it()
    {
        var (ctx, tools) = await RuntimeAsync();
        var scope = new ScopeKey("agent");
        var scoped = ctx.WithScope(scope);
        tools.Register(scoped, new EchoTool("report"));
        tools.Restrict(scoped, new ToolRestriction(Allow: ["nothing-at-all"]));

        Assert.Equal(["report"], tools.Schemas(scope).Select(static schema => schema.Name));
    }

    [Fact]
    public async Task A_guard_denial_cannot_be_overturned_by_a_permissive_listener()
    {
        var (ctx, tools) = await RuntimeAsync();
        tools.Register(ctx, new EchoTool());
        tools.Guard(ctx, _ => "the guard says no");
        ctx.OnWaterfall(ToolKeys.PreExecute, (payload, next) =>
            Task.FromResult<PreToolDecision>(AllowDecision.Instance));

        var result = await tools.ExecuteAsync(Call(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("the guard says no", Assert.IsType<TextBlock>(result.Content[0]).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_guard_that_throws_refuses_rather_than_allowing()
    {
        var (ctx, tools) = await RuntimeAsync();
        tools.Register(ctx, new EchoTool());
        tools.Guard(ctx, _ => throw new InvalidOperationException("guard exploded"));

        var result = await tools.ExecuteAsync(Call(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(ToolErrorCodes.Denied, result.Error?.Code);
    }

    [Fact]
    public async Task Execution_mode_is_exclusive_unless_a_visible_tool_says_otherwise()
    {
        var (ctx, tools) = await RuntimeAsync();
        tools.Register(ctx, new EchoTool("careful"));
        tools.Register(ctx, new EchoTool("safe") { Concurrent = true });

        Assert.Equal(ToolExecutionMode.Exclusive, tools.ExecutionMode(Call("careful")));
        Assert.Equal(ToolExecutionMode.Parallel, tools.ExecutionMode(Call("safe")));
        Assert.Equal(ToolExecutionMode.Exclusive, tools.ExecutionMode(Call("missing")));
    }

    [Fact]
    public async Task An_unknown_tool_comes_back_as_a_result_the_model_can_read()
    {
        var (_, tools) = await RuntimeAsync();

        var result = await tools.ExecuteAsync(Call("nonexistent"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(ToolErrorCodes.UnknownTool, result.Error?.Code);
    }

    [Fact]
    public async Task Arguments_that_do_not_match_the_schema_are_refused_before_the_body_runs()
    {
        var (ctx, tools) = await RuntimeAsync();
        var ran = false;
        tools.Register(ctx, new EchoTool(body: (_, _) =>
        {
            ran = true;
            return Task.FromResult(JsonValue.From(new Dictionary<string, object?> { ["echoed"] = "x" }));
        }));

        var result = await tools.ExecuteAsync(
            Call(args: JsonValue.From(new Dictionary<string, object?>())),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(ToolErrorCodes.InvalidArgs, result.Error?.Code);
        Assert.False(ran);
    }

    [Fact]
    public async Task A_value_its_own_output_schema_rejects_fails_the_call()
    {
        var (ctx, tools) = await RuntimeAsync();
        tools.Register(ctx, new EchoTool(body: (_, _) =>
            Task.FromResult(JsonValue.From(new Dictionary<string, object?> { ["wrong"] = 1 }))));

        var result = await tools.ExecuteAsync(Call(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(ToolErrorCodes.InvalidOutput, result.Error?.Code);
    }

    [Fact]
    public async Task A_body_that_throws_becomes_an_error_result_rather_than_an_exception()
    {
        var (ctx, tools) = await RuntimeAsync();
        tools.Register(ctx, new EchoTool(body: (_, _) => throw new InvalidOperationException("body exploded")));

        var result = await tools.ExecuteAsync(Call(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(ToolErrorCodes.Failed, result.Error?.Code);
        Assert.Contains("body exploded", Assert.IsType<TextBlock>(result.Content[0]).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_call_cancelled_before_dispatch_says_so_distinctly()
    {
        var (ctx, tools) = await RuntimeAsync();
        tools.Register(ctx, new EchoTool());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await tools.ExecuteAsync(Call(), cancelled.Token);

        Assert.Equal(ToolErrorCodes.AbortedBeforeDispatch, result.Error?.Code);
    }

    [Fact]
    public async Task A_pre_execute_listener_can_refuse_a_call()
    {
        var (ctx, tools) = await RuntimeAsync();
        tools.Register(ctx, new EchoTool());
        ctx.OnWaterfall(ToolKeys.PreExecute, (payload, next) =>
            Task.FromResult<PreToolDecision>(new DenyDecision("policy refused it")));

        var result = await tools.ExecuteAsync(Call(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("policy refused it", Assert.IsType<TextBlock>(result.Content[0]).Text, StringComparison.Ordinal);
    }

    private sealed class FixedApproval : IApprovalService
    {
        public FixedApproval(ApprovalOutcome outcome) => Outcome = outcome;

        public ApprovalOutcome Outcome { get; }

        public int Requests { get; private set; }

        public Task<ApprovalOutcome> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(Outcome);
        }
    }

    [Fact]
    public async Task An_ask_with_no_approval_channel_fails_closed()
    {
        var (ctx, tools) = await RuntimeAsync();
        tools.Register(ctx, new EchoTool());
        ctx.OnWaterfall(ToolKeys.PreExecute, (payload, next) =>
            Task.FromResult<PreToolDecision>(new AskDecision("needs a person")));

        var result = await tools.ExecuteAsync(Call(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("no approval channel", Assert.IsType<TextBlock>(result.Content[0]).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_approved_ask_lets_the_call_run()
    {
        var (ctx, tools) = await RuntimeAsync();
        tools.Register(ctx, new EchoTool());
        var approval = new FixedApproval(ApprovalOutcome.AllowedOnce);
        ctx.Provide(ApprovalKeys.Service, approval);
        ctx.OnWaterfall(ToolKeys.PreExecute, (payload, next) =>
            Task.FromResult<PreToolDecision>(new AskDecision("needs a person")));

        var result = await tools.ExecuteAsync(Call(), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(1, approval.Requests);
    }

    [Theory]
    [InlineData(ApprovalOutcome.Rejected)]
    [InlineData(ApprovalOutcome.Cancelled)]
    [InlineData(ApprovalOutcome.Unavailable)]
    public async Task Every_non_grant_outcome_refuses_the_call(ApprovalOutcome outcome)
    {
        var (ctx, tools) = await RuntimeAsync();
        tools.Register(ctx, new EchoTool());
        ctx.Provide(ApprovalKeys.Service, new FixedApproval(outcome));
        ctx.OnWaterfall(ToolKeys.PreExecute, (payload, next) =>
            Task.FromResult<PreToolDecision>(new AskDecision("needs a person")));

        var result = await tools.ExecuteAsync(Call(), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task An_approval_channel_that_throws_fails_closed()
    {
        var (ctx, tools) = await RuntimeAsync();
        tools.Register(ctx, new EchoTool());
        ctx.Provide(ApprovalKeys.Service, new ThrowingApproval());
        ctx.OnWaterfall(ToolKeys.PreExecute, (payload, next) =>
            Task.FromResult<PreToolDecision>(new AskDecision("needs a person")));

        var result = await tools.ExecuteAsync(Call(), CancellationToken.None);

        Assert.True(result.IsError);
    }

    private sealed class ThrowingApproval : IApprovalService
    {
        public Task<ApprovalOutcome> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("no channel");
    }

    [Fact]
    public async Task An_execute_listener_wraps_the_body()
    {
        var (ctx, tools) = await RuntimeAsync();
        tools.Register(ctx, new EchoTool());
        ctx.OnWaterfall(ToolKeys.Execute, async (payload, next) =>
        {
            var inner = await next();
            return inner with { Content = [new TextBlock($"[wrapped] {ContentBlocks.FlattenText(inner.Content)}")] };
        });

        var result = await tools.ExecuteAsync(Call(), CancellationToken.None);

        Assert.Equal("[wrapped] hi", Assert.IsType<TextBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task A_post_execute_block_turns_a_success_into_feedback_for_the_model()
    {
        var (ctx, tools) = await RuntimeAsync();
        tools.Register(ctx, new EchoTool());
        ctx.OnWaterfall(ToolKeys.PostExecute, (payload, next) =>
            Task.FromResult<PostToolDecision>(new BlockDecision([new TextBlock("do it differently")])));

        var result = await tools.ExecuteAsync(Call(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("do it differently", Assert.IsType<TextBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task A_post_execute_accept_can_replace_the_content_it_saw()
    {
        var (ctx, tools) = await RuntimeAsync();
        tools.Register(ctx, new EchoTool());
        ctx.OnWaterfall(ToolKeys.PostExecute, (payload, next) =>
            Task.FromResult<PostToolDecision>(new AcceptDecision([new TextBlock("rewritten")])));

        var result = await tools.ExecuteAsync(Call(), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("rewritten", Assert.IsType<TextBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task A_settled_call_is_announced_to_observers()
    {
        var (ctx, tools) = await RuntimeAsync();
        tools.Register(ctx, new EchoTool());
        var seen = new List<string>();
        ctx.On(ToolKeys.Result, notice => seen.Add(notice.Execution.Name));

        await tools.ExecuteAsync(Call(), CancellationToken.None);

        Assert.Equal(["echo"], seen);
    }

    [Fact]
    public async Task A_tool_can_stage_context_and_conclude_the_turn()
    {
        var (ctx, tools) = await RuntimeAsync();
        tools.Register(ctx, new EchoTool(body: (args, exec) =>
        {
            exec.DeferContext(Message.UserText("something the model should know"));
            exec.ConcludeTurn();
            return Task.FromResult(JsonValue.From(new Dictionary<string, object?> { ["echoed"] = "done" }));
        }));

        var result = await tools.ExecuteAsync(Call(), CancellationToken.None);

        Assert.True(result.ConcludesTurn);
        Assert.Equal(
            "something the model should know",
            Assert.Single(result.AdditionalContexts!).Text);
    }

    [Fact]
    public async Task A_tool_declaring_an_unenforceable_schema_is_refused_at_registration()
    {
        var (ctx, tools) = await RuntimeAsync();

        var error = Assert.Throws<InvalidOperationException>(() => tools.Register(ctx, new UnenforceableTool()));

        Assert.Contains("unsupported keyword", error.Message, StringComparison.Ordinal);
    }

    private sealed class UnenforceableTool : ToolBase
    {
        public override string Name => "regex-tool";

        public override string Description => "Uses a keyword the harness cannot check.";

        public override JsonSchemaNode Parameters { get; } = new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "object",
            ["patternProperties"] = new Dictionary<string, object?>(StringComparer.Ordinal),
        });

        public override ToolOutput Output { get; } = new(Schema.Any(), (_, _) => []);

        public override Task<JsonValue> ExecuteAsync(JsonValue args, ToolRunContext exec)
            => Task.FromResult(JsonValue.Null);
    }
}

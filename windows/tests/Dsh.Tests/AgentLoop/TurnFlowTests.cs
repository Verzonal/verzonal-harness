using Dsh.Agent;
using Dsh.Llm;
using Dsh.Llm.Fake;
using Dsh.Session;
using Dsh.Tools;

namespace Dsh.Tests.AgentLoop;

public sealed class TurnFlowTests
{
    private const string Chunk = "assistant/chunk";
    private const string Spliced = "agent/inbox/spliced";
    private const string Header = "request/header";
    private const string RouteContext = "request/context";

    private static string[] Noise => [Chunk, Spliced, Header, RouteContext];

    private static ToolCallBlock Call(string id, string name, string arguments = "{}")
        => new(new CallId(id), name, arguments);

    [Fact]
    public async Task A_prompt_drives_one_turn_with_one_step()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(ScriptedReply.Text("hello back")));

        await fixture.PromptAsync("hello");

        Assert.Equal(
            [
                "turn/start",
                "step/start",
                "user/message",
                "assistant/message",
                "step/end",
                "turn/end",
            ],
            fixture.EventTypes(Noise));
        Assert.IsType<CompletedTurnEnd>(fixture.LastTurnEnd());
    }

    [Fact]
    public async Task Derived_history_holds_the_prompt_and_the_reply()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(ScriptedReply.Text("hello back")));

        await fixture.PromptAsync("hello");

        var messages = fixture.Session.DeriveMessages();
        Assert.Equal(2, messages.Count);
        Assert.Equal("hello", messages[0].Text);
        Assert.Equal("hello back", messages[1].Text);
        Assert.Equal(MessageRole.Assistant, messages[1].Role);
    }

    [Fact]
    public async Task Every_streamed_chunk_is_logged_and_cited_by_the_message_it_built()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(ScriptedReply.Text("one two three")));

        await fixture.PromptAsync("hello");

        var chunkSeqs = fixture.Session.Events
            .Where(static entry => entry.Type == Chunk)
            .Select(static entry => entry.Seq)
            .ToArray();
        var message = fixture.Session.Events.Single(static entry => entry.Type == SessionEvents.AssistantMessage.Name);

        Assert.NotEmpty(chunkSeqs);
        Assert.Equal(chunkSeqs, message.SourceEventSeqs);
    }

    [Fact]
    public async Task The_request_the_model_received_is_exactly_what_the_log_reconstructs()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(ScriptedReply.Text("ok")));
        fixture.Prompt.Section(
            fixture.Ctx,
            Dsh.SystemPrompt.PromptSection.Fixed("persona", 0, "You are a careful assistant."));

        await fixture.PromptAsync("hello");

        var sent = Assert.Single(fixture.Adapter.Requests);
        var header = RequestHeaders.Fold(fixture.Session.Events);

        Assert.NotNull(header);
        Assert.Equal(header!.System, sent.System);
        Assert.True(header.Config.Matches(sent.Config));
        Assert.Equal("You are a careful assistant.", sent.System);
    }

    [Fact]
    public async Task The_first_header_is_recorded_as_initial_and_a_changed_route_as_a_change()
    {
        await using var fixture = await LoopFixture.StartAsync(
            new ScriptedAdapter(ScriptedReply.Text("one"), ScriptedReply.Text("two")));

        await fixture.PromptAsync("first");

        fixture.Ctx.OnWaterfall(
            AgentKeys.Request,
            (payload, next) => Task.FromResult(new LlmCallConfig(ScriptedAdapter.ProviderRoute, "another-model")),
            prepend: true);

        await fixture.PromptAsync("second");

        var reasons = fixture.Session.Events
            .Where(static entry => entry.Type == Header)
            .Select(static entry => entry.DataAs<RequestHeaderData>().Reason)
            .ToArray();

        Assert.Equal([RequestHeaderReason.Initial, RequestHeaderReason.Change], reasons);
    }

    [Fact]
    public async Task An_unchanged_header_is_not_recorded_again()
    {
        await using var fixture = await LoopFixture.StartAsync(
            new ScriptedAdapter(ScriptedReply.Text("one"), ScriptedReply.Text("two")));

        await fixture.PromptAsync("first");
        await fixture.PromptAsync("second");

        Assert.Single(fixture.Session.Events, static entry => entry.Type == Header);
    }

    [Fact]
    public async Task A_tool_call_runs_and_the_model_gets_another_step_with_the_result()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(
            ScriptedReply.ToolCalls([Call("call-1", "probe")]),
            ScriptedReply.Text("the probe said hi")));
        fixture.Tools.Register(fixture.Ctx, new ProbeTool("probe", (_, _) => Task.FromResult(ProbeTool.Text("hi"))));

        await fixture.PromptAsync("use the probe");

        Assert.Equal(
            [
                "turn/start",
                "step/start",
                "user/message",
                "assistant/message",
                "tool/call",
                "tool/result",
                "step/end",
                "step/start",
                "assistant/message",
                "step/end",
                "turn/end",
            ],
            fixture.EventTypes(Noise));

        var messages = fixture.Session.DeriveMessages();
        Assert.Equal(4, messages.Count);
        var result = Assert.IsType<ToolResultBlock>(Assert.Single(messages[2].Content));
        Assert.Equal("hi", ContentBlocks.FlattenText(result.Content));
    }

    [Fact]
    public async Task A_tool_that_concludes_the_turn_stops_the_loop_without_another_request()
    {
        await using var fixture = await LoopFixture.StartAsync(
            new ScriptedAdapter(ScriptedReply.ToolCalls([Call("call-1", "finish")])));
        fixture.Tools.Register(fixture.Ctx, new ProbeTool("finish", (_, exec) =>
        {
            exec.ConcludeTurn();
            return Task.FromResult(ProbeTool.Text("done"));
        }));

        await fixture.PromptAsync("wrap up");

        Assert.Single(fixture.Adapter.Requests);
        Assert.IsType<CompletedTurnEnd>(fixture.LastTurnEnd());
    }

    [Fact]
    public async Task Context_a_tool_stages_reaches_the_next_step()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(
            ScriptedReply.ToolCalls([Call("call-1", "notify")]),
            ScriptedReply.Text("noted")));
        fixture.Tools.Register(fixture.Ctx, new ProbeTool("notify", (_, exec) =>
        {
            exec.DeferContext(Message.Context(
                "test",
                ContextForm.Notice,
                [new TextBlock("a file changed on disk")]));
            return Task.FromResult(ProbeTool.Text("ok"));
        }));

        await fixture.PromptAsync("watch for changes");

        var second = fixture.Adapter.Requests[1];
        Assert.Contains(second.Messages, message => message.Text == "a file changed on disk");
    }

    [Fact]
    public async Task Parallel_calls_commit_their_results_in_model_order_whatever_the_finishing_order()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(
            ScriptedReply.ToolCalls([Call("call-1", "slow"), Call("call-2", "fast")]),
            ScriptedReply.Text("both done")));

        fixture.Tools.Register(fixture.Ctx, new ProbeTool("slow", async (_, _) =>
        {
            await Task.Delay(60);
            return ProbeTool.Text("slow result");
        }, concurrencySafe: true));
        fixture.Tools.Register(fixture.Ctx, new ProbeTool("fast", (_, _) =>
            Task.FromResult(ProbeTool.Text("fast result")), concurrencySafe: true));

        await fixture.PromptAsync("run both");

        var results = fixture.Session.Events
            .Where(static entry => entry.Type == SessionEvents.ToolResult.Name)
            .Select(static entry => entry.DataAs<ToolResultData>())
            .Select(static data => ContentBlocks.FlattenText(
                ((ToolResultBlock)data.Message.Content[0]).Content))
            .ToArray();

        Assert.Equal(["slow result", "fast result"], results);
    }

    [Fact]
    public async Task An_exclusive_call_forms_a_barrier_so_the_next_one_starts_after_it_finishes()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(
            ScriptedReply.ToolCalls([Call("call-1", "first"), Call("call-2", "second")]),
            ScriptedReply.Text("done")));

        var running = 0;
        var overlapped = false;

        Task<JsonValue> Body(JsonValue _, ToolRunContext __)
            => Track();

        async Task<JsonValue> Track()
        {
            if (Interlocked.Increment(ref running) > 1) overlapped = true;
            await Task.Delay(20);
            Interlocked.Decrement(ref running);
            return ProbeTool.Text("ok");
        }

        fixture.Tools.Register(fixture.Ctx, new ProbeTool("first", Body));
        fixture.Tools.Register(fixture.Ctx, new ProbeTool("second", Body));

        await fixture.PromptAsync("run both");

        Assert.False(overlapped);
        Assert.Equal(2, fixture.Session.Events.Count(static entry => entry.Type == SessionEvents.ToolResult.Name));
    }

    [Fact]
    public async Task Every_tool_call_gets_a_result_even_when_the_tool_is_unknown()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(
            ScriptedReply.ToolCalls([Call("call-1", "nonexistent")]),
            ScriptedReply.Text("I could not do that")));

        await fixture.PromptAsync("try it");

        var result = Assert.Single(
            fixture.Session.Events,
            static entry => entry.Type == SessionEvents.ToolResult.Name);
        var data = result.DataAs<ToolResultData>();
        Assert.Equal(ToolErrorCodes.UnknownTool, data.Error?.Code);
        Assert.True(((ToolResultBlock)data.Message.Content[0]).IsError);
    }

    [Fact]
    public async Task Malformed_tool_arguments_reach_the_model_as_a_schema_complaint()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(
            ScriptedReply.ToolCalls([Call("call-1", "probe", "{not json at all")]),
            ScriptedReply.Text("sorry")));
        fixture.Tools.Register(fixture.Ctx, new ProbeTool("probe", (_, _) => Task.FromResult(ProbeTool.Text("hi"))));

        await fixture.PromptAsync("use the probe");

        var data = fixture.Session.Events
            .Single(static entry => entry.Type == SessionEvents.ToolResult.Name)
            .DataAs<ToolResultData>();

        Assert.Equal(ToolErrorCodes.InvalidArgs, data.Error?.Code);
    }

    [Fact]
    public async Task Hitting_the_output_ceiling_is_recorded_and_stays_sticky_across_later_steps()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(
            ScriptedReply.Truncated("as far as I got")));

        await fixture.PromptAsync("write a lot");

        Assert.IsType<MaxTokensTurnEnd>(fixture.LastTurnEnd());
    }

    [Fact]
    public async Task A_rejected_step_closes_the_turn_as_blocked_and_spends_no_model_call()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(ScriptedReply.Text("never sent")));
        fixture.Ctx.OnWaterfall(
            AgentKeys.PreStep,
            (payload, next) => Task.FromResult<PreStepDecision>(RejectStep.Instance));

        await fixture.PromptAsync("hello");

        Assert.IsType<BlockedTurnEnd>(fixture.LastTurnEnd());
        Assert.Empty(fixture.Adapter.Requests);
        Assert.Equal(["turn/start", "turn/end"], fixture.EventTypes(Noise));
    }

    [Fact]
    public async Task A_pre_step_listener_can_rewrite_what_the_model_sees()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(ScriptedReply.Text("ok")));
        fixture.Ctx.OnWaterfall(AgentKeys.PreStep, async (payload, next) =>
        {
            var decision = await next();
            return decision is EnterStep entered
                ? new EnterStep([.. entered.Messages, Message.UserText("and be brief")])
                : decision;
        });

        await fixture.PromptAsync("explain everything");

        var sent = Assert.Single(fixture.Adapter.Requests);
        Assert.Contains(sent.Messages, message => message.Text == "and be brief");
    }

    [Fact]
    public async Task A_failed_request_ends_the_turn_with_the_providers_own_failure()
    {
        await using var fixture = await LoopFixture.StartAsync(
            new ScriptedAdapter(ScriptedReply.Failure("the provider is down", LlmErrorCodes.Server)));

        await fixture.PromptAsync("hello");

        var reason = Assert.IsType<ErrorTurnEnd>(fixture.LastTurnEnd());
        Assert.Equal(LlmErrorCodes.Server, reason.Error.Code);
        Assert.Equal("the provider is down", reason.Error.Message);
    }

    [Fact]
    public async Task A_listener_can_retry_a_failed_request_and_the_turn_completes()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(
            ScriptedReply.Failure("transient", LlmErrorCodes.RateLimit),
            ScriptedReply.Text("second time lucky")));

        var attempts = new List<int>();
        fixture.Ctx.OnWaterfall(AgentKeys.RequestError, (payload, next) =>
        {
            attempts.Add(payload.Attempt);
            return Task.FromResult<RequestErrorAction>(
                payload.Attempt < 2 ? RetryRequest.Instance : TerminalRequestFailure.Instance);
        });

        await fixture.PromptAsync("hello");

        Assert.Equal([1], attempts);
        Assert.IsType<CompletedTurnEnd>(fixture.LastTurnEnd());
        Assert.Equal("second time lucky", fixture.Session.DeriveMessages()[^1].Text);
    }

    [Fact]
    public async Task An_objector_that_steers_gets_another_step_rather_than_a_closed_turn()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(
            ScriptedReply.Text("first answer"),
            ScriptedReply.Text("second answer")));

        var objected = false;
        fixture.Ctx.OnSerial(AgentKeys.TurnStopping, payload =>
        {
            if (!objected)
            {
                objected = true;
                payload.Agent.Steer(Message.UserText("wait, also check this"));
            }

            return Task.CompletedTask;
        });

        await fixture.PromptAsync("hello");

        Assert.Equal(2, fixture.Adapter.Requests.Count);
        Assert.IsType<CompletedTurnEnd>(fixture.LastTurnEnd());
    }

    [Fact]
    public async Task Queued_prompts_each_get_their_own_turn()
    {
        await using var fixture = await LoopFixture.StartAsync(new ScriptedAdapter(
            ScriptedReply.Text("first"),
            ScriptedReply.Text("second")));
        fixture.Adapter.ChunkDelay = TimeSpan.FromMilliseconds(5);

        fixture.Agent.Followup(Message.UserText("one"));
        fixture.Agent.Followup(Message.UserText("two"));
        await fixture.Agent.WhenIdleAsync();

        var turns = fixture.Session.Events
            .Where(static entry => entry.Type == SessionEvents.TurnStart.Name)
            .Select(static entry => entry.DataAs<TurnStartData>().Turn)
            .ToArray();

        Assert.Equal([1, 2], turns);
    }

    [Fact]
    public async Task Turn_numbering_continues_from_the_log_when_a_session_is_resumed()
    {
        await using var first = await LoopFixture.StartAsync(new ScriptedAdapter(ScriptedReply.Text("one")));
        await first.PromptAsync("hello");
        var stored = first.Session.Events;

        await using var resumed = await LoopFixture.StartAsync(
            new ScriptedAdapter(ScriptedReply.Text("two")),
            configure: null);
        var revived = new Dsh.Session.Session(
            SessionStore.NewHeader(new SessionId("session-resumed"), "/workspace") with { SeedLength = stored.Count },
            stored);

        Assert.Equal(1, revived.LastTurn());
    }
}

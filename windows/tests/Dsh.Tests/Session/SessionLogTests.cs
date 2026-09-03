using Dsh.Llm;
using Dsh.Session;

namespace Dsh.Tests.Session;

public sealed class SessionLogTests
{
    private static Dsh.Session.Session NewSession(IReadOnlyList<SessionEvent>? seed = null)
        => new(SessionStore.NewHeader(new SessionId("session-1"), "/workspace"), seed);

    private static ModelMessageSource Route => new("deepseek-official", "deepseek-v4-flash");

    [Fact]
    public void Every_event_seq_equals_its_index_in_the_log()
    {
        var session = NewSession();

        session.Append(SessionEvents.TurnStart, new TurnStartData(1));
        session.Append(SessionEvents.StepStart, new StepStartData(1, 1));
        session.Append(SessionEvents.StepEnd, new StepEndData(1, 1));

        for (var index = 0; index < session.Events.Count; index++)
        {
            Assert.Equal(index, session.Events[index].Seq);
        }
    }

    [Fact]
    public void A_message_producing_event_must_declare_its_surface_placement()
    {
        var session = NewSession();

        var error = Assert.Throws<InvalidOperationException>(
            () => session.Append(SessionEvents.UserMessage, Message.UserText("hello")));

        Assert.Contains("must declare its surface placement", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_log_only_event_cannot_carry_surface_placement()
    {
        var session = NewSession();

        var error = Assert.Throws<InvalidOperationException>(() => session.Append(
            SessionEvents.TurnStart,
            new TurnStartData(1),
            new SurfaceIntent(AppendOp.Instance)));

        Assert.Contains("log-only", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rejected_append_leaves_the_log_and_surface_untouched()
    {
        var session = NewSession();
        session.Append(SessionEvents.UserMessage, Message.UserText("first"), new SurfaceIntent(AppendOp.Instance));

        Assert.Throws<InvalidOperationException>(
            () => session.Append(SessionEvents.UserMessage, Message.UserText("second")));

        Assert.Equal(1, session.Seq);
        Assert.Single(session.Surface.Nodes);
    }

    [Fact]
    public void Derived_history_projects_only_the_three_message_producing_types()
    {
        var session = NewSession();
        session.Append(SessionEvents.TurnStart, new TurnStartData(1));
        session.Append(SessionEvents.UserMessage, Message.UserText("hello"), new SurfaceIntent(AppendOp.Instance));
        session.Append(SessionEvents.AssistantChunk, new AssistantChunkData(1, 1, new TextDeltaChunk(0, "hi")));
        session.Append(
            SessionEvents.AssistantMessage,
            new AssistantMessageData(1, 1, Message.Assistant([new TextBlock("hi")], Route)),
            new SurfaceIntent(AppendOp.Instance, [2]));
        session.Append(SessionEvents.TodoWrite, new TodoWriteData([new TodoItem("do it", TodoStatus.Pending)]));

        var messages = session.DeriveMessages();

        Assert.Equal(2, messages.Count);
        Assert.Equal(MessageRole.User, messages[0].Role);
        Assert.Equal(MessageRole.Assistant, messages[1].Role);
    }

    [Fact]
    public void An_assistant_message_with_no_content_is_left_out_of_derived_history()
    {
        var session = NewSession();
        session.Append(
            SessionEvents.AssistantMessage,
            new AssistantMessageData(1, 1, Message.Assistant([], Route), new TokenUsage(10, 0)),
            new SurfaceIntent(AppendOp.Instance, []));

        Assert.Empty(session.DeriveMessages());
        Assert.Single(session.Surface.Nodes);
    }

    [Fact]
    public void A_tool_result_rides_on_a_user_message_carrying_one_tool_result_block()
    {
        var session = NewSession();
        var callId = new CallId("call-1");
        var call = session.Append(
            SessionEvents.ToolCall,
            new ToolCallData(1, 1, callId, "read", "{}"));
        session.Append(
            SessionEvents.ToolResult,
            new ToolResultData(1, 1, Message.ToolResult(callId, [new TextBlock("contents")], isError: false)),
            new SurfaceIntent(AppendOp.Instance, [call.Seq]));

        var message = Assert.Single(session.DeriveMessages());

        Assert.Equal(MessageRole.User, message.Role);
        var block = Assert.IsType<ToolResultBlock>(Assert.Single(message.Content));
        Assert.Equal(callId, block.ToolCallId);
        Assert.IsType<ToolMessageSource>(message.Source);
    }

    [Fact]
    public void A_replacement_shadows_the_nodes_it_cites_and_rebuilds_derived_history()
    {
        var session = NewSession();
        var first = session.Append(
            SessionEvents.UserMessage,
            Message.UserText("one"),
            new SurfaceIntent(AppendOp.Instance));
        var second = session.Append(
            SessionEvents.UserMessage,
            Message.UserText("two"),
            new SurfaceIntent(AppendOp.Instance));
        session.Append(SessionEvents.UserMessage, Message.UserText("three"), new SurfaceIntent(AppendOp.Instance));

        Assert.Equal(3, session.DeriveMessages().Count);

        session.Append(
            SessionEvents.UserMessage,
            Message.UserText("summary of one and two"),
            new SurfaceIntent(new ReplaceOp(first.Seq, second.Seq), [first.Seq, second.Seq]));

        var messages = session.DeriveMessages();

        Assert.Equal(2, messages.Count);
        Assert.Equal("summary of one and two", messages[0].Text);
        Assert.Equal("three", messages[1].Text);

        // The shadowed events stay in the log; only derivation drops them.
        Assert.Equal(4, session.Events.Count);
    }

    [Fact]
    public void A_replacement_must_cite_every_node_it_shadows()
    {
        var session = NewSession();
        var first = session.Append(
            SessionEvents.UserMessage,
            Message.UserText("one"),
            new SurfaceIntent(AppendOp.Instance));
        var second = session.Append(
            SessionEvents.UserMessage,
            Message.UserText("two"),
            new SurfaceIntent(AppendOp.Instance));

        var error = Assert.Throws<InvalidOperationException>(() => session.Append(
            SessionEvents.UserMessage,
            Message.UserText("summary"),
            new SurfaceIntent(new ReplaceOp(first.Seq, second.Seq), [first.Seq])));

        Assert.Contains("without citing it", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_citation_cannot_name_a_later_event()
    {
        var session = NewSession();

        var error = Assert.Throws<InvalidOperationException>(() => session.Append(
            SessionEvents.UserMessage,
            Message.UserText("hello"),
            new SurfaceIntent(AppendOp.Instance, [5])));

        Assert.Contains("not an earlier event", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_replacement_naming_a_seq_that_is_not_a_surface_node_is_refused()
    {
        var session = NewSession();
        session.Append(SessionEvents.TurnStart, new TurnStartData(1));

        var error = Assert.Throws<InvalidOperationException>(() => session.Append(
            SessionEvents.UserMessage,
            Message.UserText("summary"),
            new SurfaceIntent(new ReplaceOp(0, 0), [0])));

        Assert.Contains("not a current surface node", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_append_cannot_re_enter_from_inside_a_listener()
    {
        var session = NewSession();
        Exception? captured = null;
        session.OnEvent(_ =>
        {
            try
            {
                session.Append(SessionEvents.TurnStart, new TurnStartData(99));
            }
            catch (Exception error)
            {
                captured = error;
            }
        });

        session.Append(SessionEvents.TurnStart, new TurnStartData(1));

        Assert.IsType<InvalidOperationException>(captured);
        Assert.Contains("cannot re-enter", captured!.Message, StringComparison.Ordinal);
        Assert.Equal(1, session.Seq);
    }

    [Fact]
    public async Task A_concurrent_append_waits_its_turn_rather_than_reading_as_re_entrancy()
    {
        // The desktop app does exactly this: a person types while a turn is streaming,
        // so the UI thread appends while the loop's thread is mid-publication. That is
        // ordinary concurrency, not a listener re-entering its own event, and it must
        // serialize rather than be refused.
        var session = NewSession();
        var publishing = new ManualResetEventSlim();
        var arriving = new ManualResetEventSlim();
        Exception? failure = null;

        session.OnEvent(entry =>
        {
            if (entry.Seq != 0) return;
            publishing.Set();

            // Hold publication open until the other thread is at the guard, then a
            // moment longer so it is genuinely blocked inside it.
            arriving.Wait(TimeSpan.FromSeconds(10));
            Thread.Sleep(50);
        });

        var other = Task.Run(() =>
        {
            publishing.Wait(TimeSpan.FromSeconds(10));
            arriving.Set();

            try
            {
                session.Append(SessionEvents.TurnStart, new TurnStartData(2));
            }
            catch (Exception error)
            {
                failure = error;
            }
        });

        session.Append(SessionEvents.TurnStart, new TurnStartData(1));
        await other.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(failure);
        Assert.Equal(2, session.Seq);
        Assert.Equal([0, 1], session.Events.Select(static entry => entry.Seq));
    }

    [Fact]
    public void A_listener_registered_during_dispatch_does_not_observe_that_event()
    {
        var session = NewSession();
        var seen = new List<string>();
        session.OnEvent(_ => session.OnEvent(later => seen.Add(later.Type)));

        session.Append(SessionEvents.TurnStart, new TurnStartData(1));
        Assert.Empty(seen);

        session.Append(SessionEvents.TurnStart, new TurnStartData(2));
        Assert.Single(seen);
    }

    [Fact]
    public void A_snapshot_handed_out_earlier_never_grows()
    {
        var session = NewSession();
        session.Append(SessionEvents.TurnStart, new TurnStartData(1));
        var snapshot = session.Events;

        session.Append(SessionEvents.TurnEnd, new TurnEndData(1, CompletedTurnEnd.Instance));

        Assert.Single(snapshot);
        Assert.Equal(2, session.Events.Count);
    }

    [Fact]
    public void The_request_header_fold_returns_the_latest_snapshot()
    {
        var session = NewSession();
        var first = new EpochHeader(new LlmCallConfig("deepseek-official", "deepseek-v4-flash"));
        var second = new EpochHeader(new LlmCallConfig("deepseek-official", "deepseek-v4-pro"));

        Assert.Null(session.RequestHeader());

        session.Append(SessionEvents.RequestHeader, new RequestHeaderData(first, RequestHeaderReason.Initial));
        Assert.Equal("deepseek-v4-flash", session.RequestHeader()?.Config.Model);

        session.Append(SessionEvents.RequestHeader, new RequestHeaderData(second, RequestHeaderReason.Change));
        Assert.Equal("deepseek-v4-pro", session.RequestHeader()?.Config.Model);
    }

    [Fact]
    public void The_todo_fold_returns_the_latest_whole_list()
    {
        var session = NewSession();
        Assert.Null(session.Todos());

        session.Append(SessionEvents.TodoWrite, new TodoWriteData([new TodoItem("first", TodoStatus.Pending)]));
        session.Append(SessionEvents.TodoWrite, new TodoWriteData([
            new TodoItem("first", TodoStatus.Completed),
            new TodoItem("second", TodoStatus.InProgress),
        ]));

        var todos = session.Todos();
        Assert.Equal(2, todos?.Count);
        Assert.Equal(TodoStatus.Completed, todos![0].Status);
    }

    [Fact]
    public void Last_turn_recovers_numbering_so_a_resumed_agent_continues_it()
    {
        var session = NewSession();
        session.Append(SessionEvents.TurnStart, new TurnStartData(1));
        session.Append(SessionEvents.TurnEnd, new TurnEndData(1, CompletedTurnEnd.Instance));
        session.Append(SessionEvents.TurnStart, new TurnStartData(2));
        session.Append(SessionEvents.TurnEnd, new TurnEndData(2, CompletedTurnEnd.Instance));

        Assert.Equal(2, session.LastTurn());
    }
}

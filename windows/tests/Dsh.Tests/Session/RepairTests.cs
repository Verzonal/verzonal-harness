using Dsh.Llm;
using Dsh.Session;

namespace Dsh.Tests.Session;

public sealed class RepairTests
{
    private static Dsh.Session.Session NewSession()
        => new(SessionStore.NewHeader(new SessionId("session-1"), "/workspace"));

    [Fact]
    public void A_log_ending_cleanly_needs_no_repair()
    {
        var session = NewSession();
        session.Append(SessionEvents.TurnStart, new TurnStartData(1));
        session.Append(SessionEvents.TurnEnd, new TurnEndData(1, CompletedTurnEnd.Instance));

        Assert.Empty(SessionRepair.InterruptedTurnClosers(session.Events));
    }

    [Fact]
    public void A_turn_left_open_is_closed_as_interrupted()
    {
        var session = NewSession();
        session.Append(SessionEvents.TurnStart, new TurnStartData(3));

        var closers = SessionRepair.InterruptedTurnClosers(session.Events);

        var closer = Assert.Single(closers);
        Assert.Equal(SessionEvents.TurnEnd.Name, closer.Type);
        var data = Assert.IsType<TurnEndData>(closer.Data);
        Assert.Equal(3, data.Turn);
        Assert.IsType<InterruptedTurnEnd>(data.Reason);
    }

    [Fact]
    public void An_open_step_is_closed_before_its_turn()
    {
        var session = NewSession();
        session.Append(SessionEvents.TurnStart, new TurnStartData(1));
        session.Append(SessionEvents.StepStart, new StepStartData(1, 2));

        var closers = SessionRepair.InterruptedTurnClosers(session.Events);

        Assert.Equal(2, closers.Count);
        Assert.Equal(SessionEvents.StepEnd.Name, closers[0].Type);
        Assert.Equal(2, Assert.IsType<StepEndData>(closers[0].Data).Step);
        Assert.Equal(SessionEvents.TurnEnd.Name, closers[1].Type);
    }

    [Fact]
    public void An_unanswered_tool_call_gets_a_synthetic_result_citing_it()
    {
        var session = NewSession();
        session.Append(SessionEvents.TurnStart, new TurnStartData(1));
        session.Append(SessionEvents.StepStart, new StepStartData(1, 1));
        var call = session.Append(
            SessionEvents.ToolCall,
            new ToolCallData(1, 1, new CallId("call-1"), "bash", "{}"));

        var closers = SessionRepair.InterruptedTurnClosers(session.Events);

        Assert.Equal(3, closers.Count);
        Assert.Equal(SessionEvents.ToolResult.Name, closers[0].Type);
        var result = Assert.IsType<ToolResultData>(closers[0].Data);
        Assert.Equal(SessionRepair.OutcomeUnknownCode, result.Error?.Code);
        var block = Assert.IsType<ToolResultBlock>(Assert.Single(result.Message.Content));
        Assert.True(block.IsError);
        Assert.Equal([call.Seq], closers[0].Intent?.SourceEventSeqs);
    }

    [Fact]
    public void A_call_that_already_has_its_result_needs_no_synthetic_one()
    {
        var session = NewSession();
        session.Append(SessionEvents.TurnStart, new TurnStartData(1));
        session.Append(SessionEvents.StepStart, new StepStartData(1, 1));
        var callId = new CallId("call-1");
        var call = session.Append(SessionEvents.ToolCall, new ToolCallData(1, 1, callId, "bash", "{}"));
        session.Append(
            SessionEvents.ToolResult,
            new ToolResultData(1, 1, Message.ToolResult(callId, [new TextBlock("done")], isError: false)),
            new SurfaceIntent(AppendOp.Instance, [call.Seq]));

        var closers = SessionRepair.InterruptedTurnClosers(session.Events);

        Assert.Equal(2, closers.Count);
        Assert.Equal(SessionEvents.StepEnd.Name, closers[0].Type);
    }

    [Fact]
    public void Repair_closers_append_cleanly_onto_the_log_they_were_derived_from()
    {
        var session = NewSession();
        session.Append(SessionEvents.TurnStart, new TurnStartData(1));
        session.Append(SessionEvents.StepStart, new StepStartData(1, 1));
        session.Append(SessionEvents.ToolCall, new ToolCallData(1, 1, new CallId("call-1"), "bash", "{}"));

        foreach (var closer in SessionRepair.InterruptedTurnClosers(session.Events))
        {
            if (closer.Type == SessionEvents.ToolResult.Name)
            {
                session.Append(SessionEvents.ToolResult, (ToolResultData)closer.Data, closer.Intent);
            }
            else if (closer.Type == SessionEvents.StepEnd.Name)
            {
                session.Append(SessionEvents.StepEnd, (StepEndData)closer.Data);
            }
            else
            {
                session.Append(SessionEvents.TurnEnd, (TurnEndData)closer.Data);
            }
        }

        Assert.Empty(SessionRepair.InterruptedTurnClosers(session.Events));
        Assert.Single(session.DeriveMessages());
    }
}

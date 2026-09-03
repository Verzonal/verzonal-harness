using Dsh.Cordis;
using Dsh.Llm;
using Dsh.Session;

namespace Dsh.Tests.Session;

public sealed class SessionSeedAndForkTests
{
    private static SessionHeader Header(string id = "session-1")
        => SessionStore.NewHeader(new SessionId(id), "/workspace");

    private static IReadOnlyList<SessionEvent> CompletedTurn()
    {
        var session = new Dsh.Session.Session(Header());
        session.Append(SessionEvents.TurnStart, new TurnStartData(1));
        session.Append(SessionEvents.UserMessage, Message.UserText("hello"), new SurfaceIntent(AppendOp.Instance));
        session.Append(SessionEvents.TurnEnd, new TurnEndData(1, CompletedTurnEnd.Instance));
        return session.Events;
    }

    [Fact]
    public void A_seeded_session_marks_the_boundary_between_inherited_and_live_history()
    {
        var seed = CompletedTurn();

        var session = new Dsh.Session.Session(Header("session-2"), seed);

        Assert.Equal(seed.Count, session.FirstLiveSeq);
        Assert.Equal(SessionEvents.EndSeed.Name, session.Events[^1].Type);
        Assert.Equal(seed.Count + 1, session.Events.Count);
    }

    [Fact]
    public void Reopening_an_already_marked_seed_does_not_grow_the_log_again()
    {
        var first = new Dsh.Session.Session(Header("session-2"), CompletedTurn());
        var stored = first.Events;

        var second = new Dsh.Session.Session(Header("session-3"), stored);

        Assert.Equal(stored.Count, second.Events.Count);
    }

    [Fact]
    public void A_seed_with_a_gap_in_its_seqs_is_refused()
    {
        var seed = new[]
        {
            new SessionEvent
            {
                Type = SessionEvents.TurnStart.Name,
                Seq = 0,
                Time = 1,
                Data = new TurnStartData(1),
            },
            new SessionEvent
            {
                Type = SessionEvents.TurnEnd.Name,
                Seq = 5,
                Time = 2,
                Data = new TurnEndData(1, CompletedTurnEnd.Instance),
            },
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => new Dsh.Session.Session(Header(), seed));

        Assert.Contains("not contiguous", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrecognized_required_event_makes_the_session_refuse_to_load()
    {
        var seed = new[]
        {
            new SessionEvent
            {
                Type = "plugin/from-the-future",
                Seq = 0,
                Time = 1,
                Data = new TurnStartData(1),
            },
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => new Dsh.Session.Session(Header(), seed));

        Assert.Contains("refusing to reconstruct", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrecognized_event_marked_ignorable_loads_fine()
    {
        var seed = new[]
        {
            new SessionEvent
            {
                Type = "plugin/from-the-future",
                Seq = 0,
                Time = 1,
                Data = new TurnStartData(1),
                Ignorable = true,
            },
        };

        var session = new Dsh.Session.Session(Header(), seed);

        Assert.Equal(2, session.Events.Count);
    }

    [Fact]
    public void Derived_history_survives_a_round_trip_through_a_seed()
    {
        var original = new Dsh.Session.Session(Header());
        original.Append(SessionEvents.TurnStart, new TurnStartData(1));
        original.Append(SessionEvents.UserMessage, Message.UserText("hello"), new SurfaceIntent(AppendOp.Instance));
        original.Append(
            SessionEvents.AssistantMessage,
            new AssistantMessageData(
                1,
                1,
                Message.Assistant([new TextBlock("hi")], new ModelMessageSource("deepseek-official", "deepseek-v4-flash"))),
            new SurfaceIntent(AppendOp.Instance, []));
        original.Append(SessionEvents.TurnEnd, new TurnEndData(1, CompletedTurnEnd.Instance));

        var resumed = new Dsh.Session.Session(Header("session-2"), original.Events);

        Assert.Equal(
            original.DeriveMessages().Select(static message => message.Text),
            resumed.DeriveMessages().Select(static message => message.Text));
    }

    private static async Task<(Context Ctx, SessionStore Store)> StoreAsync()
    {
        var ctx = Context.CreateRoot();
        var fiber = ctx.Plugin(SessionStore.Plugin());
        await fiber.WhenSettledAsync();
        return (ctx, ctx.Require<SessionStore>(SessionKeys.Service));
    }

    [Fact]
    public async Task Creating_a_session_announces_it_once()
    {
        var (ctx, store) = await StoreAsync();
        var created = new List<SessionId>();
        ctx.On(SessionKeys.Created, session => created.Add(session.Id));

        var (session, _) = store.Create(Header());

        Assert.Equal([session.Id], created);
    }

    [Fact]
    public async Task A_vetoing_creation_listener_rolls_the_registration_back()
    {
        var (ctx, store) = await StoreAsync();
        var disposed = new List<SessionId>();
        ctx.On(SessionKeys.Disposed, session => disposed.Add(session.Id));

        var prepared = store.Prepare(Header());
        var detach = store.Enter(prepared.Session);
        detach.Dispose();

        Assert.Null(store.Get(prepared.Session.Id));
        Assert.Empty(disposed);
    }

    [Fact]
    public async Task Detaching_an_announced_session_reports_its_disposal()
    {
        var (ctx, store) = await StoreAsync();
        var disposed = new List<SessionId>();
        ctx.On(SessionKeys.Disposed, session => disposed.Add(session.Id));

        var (session, detach) = store.Create(Header());
        detach.Dispose();

        Assert.Equal([session.Id], disposed);
        Assert.Null(store.Get(session.Id));
    }

    [Fact]
    public async Task A_published_session_broadcasts_its_appends()
    {
        var (ctx, store) = await StoreAsync();
        var seen = new List<string>();
        ctx.On(SessionKeys.Event, notice => seen.Add(notice.Event.Type));

        var (session, _) = store.Create(Header());
        session.Append(SessionEvents.TurnStart, new TurnStartData(1));

        Assert.Equal([SessionEvents.TurnStart.Name], seen);
    }

    [Fact]
    public async Task Forking_inherits_the_prefix_and_records_the_lineage()
    {
        var (_, store) = await StoreAsync();
        var (source, _) = store.Create(Header());
        source.Append(SessionEvents.TurnStart, new TurnStartData(1));
        source.Append(SessionEvents.UserMessage, Message.UserText("hello"), new SurfaceIntent(AppendOp.Instance));
        source.Append(SessionEvents.TurnEnd, new TurnEndData(1, CompletedTurnEnd.Instance));

        var (child, _) = store.Fork(source);

        Assert.Equal(source.Id, child.Header.ParentSession);
        Assert.Equal(3, child.Header.SeedLength);
        Assert.Equal("hello", Assert.Single(child.DeriveMessages()).Text);
    }

    [Fact]
    public async Task Forking_inside_an_open_turn_is_refused()
    {
        var (_, store) = await StoreAsync();
        var (source, _) = store.Create(Header());
        source.Append(SessionEvents.TurnStart, new TurnStartData(1));
        source.Append(SessionEvents.UserMessage, Message.UserText("hello"), new SurfaceIntent(AppendOp.Instance));

        var error = Assert.Throws<SessionForkException>(() => store.Fork(source));

        Assert.Equal("OPEN_TURN", error.Code);
    }

    [Fact]
    public async Task Forking_past_the_end_of_the_log_is_refused()
    {
        var (_, store) = await StoreAsync();
        var (source, _) = store.Create(Header());

        var error = Assert.Throws<SessionForkException>(() => store.Fork(source, 7));

        Assert.Equal("INVALID_BOUNDARY", error.Code);
    }

    [Fact]
    public async Task Flush_awaits_every_persistence_listener()
    {
        var (ctx, store) = await StoreAsync();
        var committed = false;
        ctx.OnParallel(SessionKeys.Flush, async _ =>
        {
            await Task.Delay(10);
            committed = true;
        });

        var (session, _) = store.Create(Header());
        await store.FlushAsync(session);

        Assert.True(committed);
    }
}

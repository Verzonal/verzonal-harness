using Dsh.Cordis;

namespace Dsh.Tests.Cordis;

public sealed class EventDispatchTests
{
    private static readonly EmitKey<string> Noticed = new("test/noticed");
    private static readonly ParallelKey<string> Flushed = new("test/flushed");
    private static readonly SerialKey<string> Stopping = new("test/stopping");
    private static readonly WaterfallKey<string, string> Decided = new("test/decided");

    [Fact]
    public void Emit_reaches_listeners_in_registration_order()
    {
        var ctx = Context.CreateRoot();
        var seen = new List<string>();
        ctx.On(Noticed, value => seen.Add($"first:{value}"));
        ctx.On(Noticed, value => seen.Add($"second:{value}"));

        ctx.Emit(Noticed, "x");

        Assert.Equal(["first:x", "second:x"], seen);
    }

    [Fact]
    public void Emit_contains_a_failing_listener_so_the_producer_continues()
    {
        var ctx = Context.CreateRoot();
        var seen = new List<string>();
        ctx.On(Noticed, _ => throw new InvalidOperationException("observer broke"));
        ctx.On(Noticed, value => seen.Add(value));

        ctx.Emit(Noticed, "x");

        Assert.Equal(["x"], seen);
    }

    [Fact]
    public void Prepend_runs_a_listener_ahead_of_ordinary_registrations()
    {
        var ctx = Context.CreateRoot();
        var seen = new List<string>();
        ctx.On(Noticed, _ => seen.Add("ordinary"));
        ctx.On(Noticed, _ => seen.Add("prepended"), prepend: true);

        ctx.Emit(Noticed, "x");

        Assert.Equal(["prepended", "ordinary"], seen);
    }

    [Fact]
    public void A_listener_registered_during_dispatch_does_not_observe_that_dispatch()
    {
        var ctx = Context.CreateRoot();
        var seen = new List<string>();
        ctx.On(Noticed, _ => ctx.On(Noticed, value => seen.Add($"late:{value}")));

        ctx.Emit(Noticed, "first");
        Assert.Empty(seen);

        ctx.Emit(Noticed, "second");
        Assert.Equal(["late:second"], seen);
    }

    [Fact]
    public void A_disposed_listener_stops_receiving_events()
    {
        var ctx = Context.CreateRoot();
        var seen = new List<string>();
        var registration = ctx.On(Noticed, value => seen.Add(value));

        ctx.Emit(Noticed, "before");
        registration.Dispose();
        ctx.Emit(Noticed, "after");

        Assert.Equal(["before"], seen);
    }

    [Fact]
    public async Task Parallel_awaits_every_listener_and_rethrows_the_first_failure_last()
    {
        var ctx = Context.CreateRoot();
        var completed = new List<string>();
        ctx.OnParallel(Flushed, async _ =>
        {
            await Task.Yield();
            completed.Add("slow");
            throw new InvalidOperationException("first failure");
        });
        ctx.OnParallel(Flushed, async _ =>
        {
            await Task.Yield();
            completed.Add("second");
            throw new InvalidOperationException("second failure");
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.ParallelAsync(Flushed, "x"));

        Assert.Equal("first failure", error.Message);
        Assert.Equal(2, completed.Count);
    }

    [Fact]
    public async Task Serial_runs_listeners_one_at_a_time_in_order()
    {
        var ctx = Context.CreateRoot();
        var seen = new List<string>();
        ctx.OnSerial(Stopping, async _ =>
        {
            await Task.Delay(10);
            seen.Add("first");
        });
        ctx.OnSerial(Stopping, _ =>
        {
            seen.Add("second");
            return Task.CompletedTask;
        });

        await ctx.SerialAsync(Stopping, "x");

        Assert.Equal(["first", "second"], seen);
    }

    [Fact]
    public async Task Serial_propagates_a_listener_failure_to_the_producer()
    {
        var ctx = Context.CreateRoot();
        ctx.OnSerial(Stopping, _ => throw new InvalidOperationException("objection"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.SerialAsync(Stopping, "x"));

        Assert.Equal("objection", error.Message);
    }

    [Fact]
    public async Task Waterfall_composes_listeners_around_the_producer_default()
    {
        var ctx = Context.CreateRoot();
        ctx.OnWaterfall(Decided, async (payload, next) => $"outer({await next()})");
        ctx.OnWaterfall(Decided, async (payload, next) => $"inner({await next()})");

        var result = await ctx.WaterfallAsync(Decided, "x", () => Task.FromResult("default"));

        Assert.Equal("outer(inner(default))", result);
    }

    [Fact]
    public async Task A_waterfall_listener_that_skips_next_short_circuits_the_rest_of_the_chain()
    {
        var ctx = Context.CreateRoot();
        var reached = false;
        ctx.OnWaterfall(Decided, (payload, next) => Task.FromResult("owned"));
        ctx.OnWaterfall(Decided, (payload, next) =>
        {
            reached = true;
            return next();
        });

        var result = await ctx.WaterfallAsync(Decided, "x", () => Task.FromResult("default"));

        Assert.Equal("owned", result);
        Assert.False(reached);
    }

    [Fact]
    public async Task Waterfall_with_no_listeners_returns_the_producer_default()
    {
        var ctx = Context.CreateRoot();

        var result = await ctx.WaterfallAsync(Decided, "x", () => Task.FromResult("default"));

        Assert.Equal("default", result);
    }

    [Fact]
    public void A_scoped_listener_only_observes_its_own_boundary()
    {
        var ctx = Context.CreateRoot();
        var mine = new ScopeKey("mine");
        var other = new ScopeKey("other");
        var seen = new List<string>();

        ctx.WithScope(mine).On(Noticed, value => seen.Add($"scoped:{value}"));
        ctx.On(Noticed, value => seen.Add($"global:{value}"));

        ctx.Emit(Noticed, "a", mine);
        ctx.Emit(Noticed, "b", other);

        Assert.Equal(["scoped:a", "global:a", "global:b"], seen);
    }
}

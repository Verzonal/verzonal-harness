using Dsh.Cordis;

namespace Dsh.Tests.Cordis;

public sealed class ContextServiceTests
{
    private sealed class Counter
    {
        public int Value { get; set; }
    }

    [Fact]
    public void Provide_publishes_the_service_under_its_key()
    {
        var ctx = Context.CreateRoot();
        var counter = new Counter();

        ctx.Provide("counter", counter);

        Assert.True(ctx.Has("counter"));
        Assert.Same(counter, ctx.Get<Counter>("counter"));
        Assert.Same(counter, ctx.Require<Counter>("counter"));
    }

    [Fact]
    public void Get_returns_null_for_an_unclaimed_key()
    {
        var ctx = Context.CreateRoot();

        Assert.Null(ctx.Get<Counter>("missing"));
        Assert.False(ctx.Has("missing"));
    }

    [Fact]
    public void Require_throws_for_an_unclaimed_key()
    {
        var ctx = Context.CreateRoot();

        var error = Assert.Throws<InvalidOperationException>(() => ctx.Require<Counter>("missing"));
        Assert.Contains("missing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Require_throws_when_the_published_service_has_another_type()
    {
        var ctx = Context.CreateRoot();
        ctx.Provide("counter", new Counter());

        Assert.Throws<InvalidOperationException>(() => ctx.Require<string>("counter"));
    }

    [Fact]
    public void A_claimed_key_cannot_be_claimed_twice()
    {
        var ctx = Context.CreateRoot();
        ctx.Provide("counter", new Counter());

        var error = Assert.Throws<InvalidOperationException>(() => ctx.Provide("counter", new Counter()));
        Assert.Contains("already provided", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Revoking_a_service_frees_its_key()
    {
        var ctx = Context.CreateRoot();
        var registration = ctx.Provide("counter", new Counter());

        registration.Dispose();

        Assert.False(ctx.Has("counter"));
        ctx.Provide("counter", new Counter());
    }

    [Fact]
    public void Extend_carries_an_ambient_value_without_touching_services()
    {
        var ctx = Context.CreateRoot();
        var counter = new Counter();

        var extended = ctx.Extend("agent", counter);

        Assert.Same(counter, extended.Value<Counter>("agent"));
        Assert.Null(ctx.Value<Counter>("agent"));
    }
}

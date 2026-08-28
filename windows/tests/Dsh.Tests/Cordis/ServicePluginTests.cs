using Dsh.Cordis;

namespace Dsh.Tests.Cordis;

public sealed class ServicePluginTests
{
    private sealed class Ledger
    {
        public List<string> Entries { get; } = [];
    }

    private class Greeter : Service
    {
        private readonly Ledger _ledger;

        public Greeter(Context ctx, Ledger ledger) : base(ctx, "greeter") => _ledger = ledger;

        public string Greet(string name) => $"hello {name}";

        public override Task StartAsync()
        {
            _ledger.Entries.Add("start");
            return Task.CompletedTask;
        }

        public override ValueTask StopAsync()
        {
            _ledger.Entries.Add("stop");
            return default;
        }
    }

    [Fact]
    public async Task A_mounted_service_starts_before_it_becomes_reachable()
    {
        var ctx = Context.CreateRoot();
        var ledger = new Ledger();
        var reachableDuringStart = false;

        var fiber = ctx.Plugin(ServicePlugin.Create("greeter", "greeter", c =>
        {
            reachableDuringStart = c.Has("greeter");
            return new Greeter(c, ledger);
        }));
        await fiber.WhenSettledAsync();

        Assert.False(reachableDuringStart);
        Assert.Equal(["start"], ledger.Entries);
        Assert.Equal("hello world", ctx.Require<Greeter>("greeter").Greet("world"));
    }

    [Fact]
    public async Task Unmounting_revokes_the_service_before_stopping_it()
    {
        var ctx = Context.CreateRoot();
        var ledger = new Ledger();
        var reachableDuringStop = true;

        var fiber = ctx.Plugin(ServicePlugin.Create<Greeter>("greeter", "greeter", c => new StoppingGreeter(
            c,
            ledger,
            () => reachableDuringStop = c.Has("greeter"))));
        await fiber.WhenSettledAsync();

        await fiber.DisposeAsync();

        Assert.False(reachableDuringStop);
        Assert.False(ctx.Has("greeter"));
        Assert.Equal(["start", "stop"], ledger.Entries);
    }

    private sealed class StoppingGreeter : Greeter
    {
        private readonly Action _probe;

        public StoppingGreeter(Context ctx, Ledger ledger, Action probe) : base(ctx, ledger) => _probe = probe;

        public override ValueTask StopAsync()
        {
            _probe();
            return base.StopAsync();
        }
    }

    [Fact]
    public async Task A_consumer_activates_only_once_the_service_it_injects_is_mounted()
    {
        var ctx = Context.CreateRoot();
        var ledger = new Ledger();
        string? greeting = null;

        var consumer = ctx.Plugin(new FunctionPlugin(
            "consumer",
            c =>
            {
                greeting = c.Require<Greeter>("greeter").Greet("consumer");
                return Task.CompletedTask;
            },
            "greeter"));
        await consumer.WhenSettledAsync();
        Assert.Null(greeting);

        var provider = ctx.Plugin(ServicePlugin.Create("greeter", "greeter", c => new Greeter(c, ledger)));
        await provider.WhenSettledAsync();
        await consumer.WhenSettledAsync();

        Assert.Equal("hello consumer", greeting);
    }
}

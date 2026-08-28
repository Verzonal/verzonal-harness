using Dsh.Cordis;

namespace Dsh.Tests.Cordis;

public sealed class FiberLifecycleTests
{
    private sealed class Recorder
    {
        public List<string> Entries { get; } = [];
    }

    private static IPlugin RecordingPlugin(string name, Recorder recorder, params string[] inject)
        => new FunctionPlugin(
            name,
            ctx =>
            {
                recorder.Entries.Add($"apply:{name}");
                ctx.Effect(new ActionDisposable(() => recorder.Entries.Add($"unwind:{name}")));
                return Task.CompletedTask;
            },
            inject);

    [Fact]
    public async Task A_plugin_with_no_injections_applies_immediately()
    {
        var ctx = Context.CreateRoot();
        var recorder = new Recorder();

        var fiber = ctx.Plugin(RecordingPlugin("simple", recorder));
        await fiber.WhenSettledAsync();

        Assert.Equal(FiberState.Active, fiber.State);
        Assert.Equal(["apply:simple"], recorder.Entries);
    }

    [Fact]
    public async Task A_plugin_waits_until_every_injected_service_exists()
    {
        var ctx = Context.CreateRoot();
        var recorder = new Recorder();

        var fiber = ctx.Plugin(RecordingPlugin("consumer", recorder, "alpha", "beta"));
        await fiber.WhenSettledAsync();
        Assert.Equal(FiberState.Pending, fiber.State);
        Assert.Empty(recorder.Entries);

        ctx.Provide("alpha", new object());
        await fiber.WhenSettledAsync();
        Assert.Equal(FiberState.Pending, fiber.State);

        ctx.Provide("beta", new object());
        await fiber.WhenSettledAsync();
        Assert.Equal(FiberState.Active, fiber.State);
        Assert.Equal(["apply:consumer"], recorder.Entries);
    }

    [Fact]
    public async Task Losing_an_injected_service_unwinds_the_plugin_and_regaining_it_reapplies()
    {
        var ctx = Context.CreateRoot();
        var recorder = new Recorder();
        var alpha = ctx.Provide("alpha", new object());

        var fiber = ctx.Plugin(RecordingPlugin("consumer", recorder, "alpha"));
        await fiber.WhenSettledAsync();
        Assert.Equal(FiberState.Active, fiber.State);

        alpha.Dispose();
        await fiber.WhenSettledAsync();
        Assert.Equal(FiberState.Pending, fiber.State);

        ctx.Provide("alpha", new object());
        await fiber.WhenSettledAsync();
        Assert.Equal(FiberState.Active, fiber.State);

        Assert.Equal(["apply:consumer", "unwind:consumer", "apply:consumer"], recorder.Entries);
    }

    [Fact]
    public async Task Effects_unwind_in_reverse_registration_order()
    {
        var ctx = Context.CreateRoot();
        var order = new List<string>();

        var fiber = ctx.Plugin(new FunctionPlugin("ordered", c =>
        {
            c.Effect(new ActionDisposable(() => order.Add("first")));
            c.Effect(new ActionDisposable(() => order.Add("second")));
            c.Effect(new ActionDisposable(() => order.Add("third")));
            return Task.CompletedTask;
        }));
        await fiber.WhenSettledAsync();

        await fiber.DisposeAsync();

        Assert.Equal(["third", "second", "first"], order);
    }

    [Fact]
    public async Task Unmounting_a_plugin_removes_its_contributions()
    {
        var ctx = Context.CreateRoot();
        var key = new EmitKey<string>("test/contribution");
        var seen = new List<string>();

        var fiber = ctx.Plugin(new FunctionPlugin("contributor", c =>
        {
            c.On(key, value => seen.Add(value));
            c.Provide("contributed", new object());
            return Task.CompletedTask;
        }));
        await fiber.WhenSettledAsync();

        ctx.Emit(key, "before");
        Assert.True(ctx.Has("contributed"));

        await fiber.DisposeAsync();

        ctx.Emit(key, "after");
        Assert.Equal(["before"], seen);
        Assert.False(ctx.Has("contributed"));
        Assert.Equal(FiberState.Disposed, fiber.State);
    }

    [Fact]
    public async Task Unmounting_a_parent_unmounts_the_children_it_mounted()
    {
        var ctx = Context.CreateRoot();
        var recorder = new Recorder();

        var parent = ctx.Plugin(new FunctionPlugin("parent", c =>
        {
            recorder.Entries.Add("apply:parent");
            c.Plugin(RecordingPlugin("child", recorder));
            c.Effect(new ActionDisposable(() => recorder.Entries.Add("unwind:parent")));
            return Task.CompletedTask;
        }));
        await parent.WhenSettledAsync();
        await Task.Delay(20);

        await parent.DisposeAsync();

        Assert.Equal(
            ["apply:parent", "apply:child", "unwind:parent", "unwind:child"],
            recorder.Entries);
    }

    [Fact]
    public async Task A_disposed_fiber_rejects_new_registrations()
    {
        var ctx = Context.CreateRoot();
        Context? captured = null;
        var fiber = ctx.Plugin(new FunctionPlugin("capturing", c =>
        {
            captured = c;
            return Task.CompletedTask;
        }));
        await fiber.WhenSettledAsync();
        await fiber.DisposeAsync();

        Assert.NotNull(captured);
        Assert.Throws<ObjectDisposedException>(
            () => captured!.Effect(new ActionDisposable(static () => { })));
    }

    [Fact]
    public async Task A_failing_apply_unwinds_its_partial_effects_and_reports_the_failure()
    {
        var ctx = Context.CreateRoot();
        var order = new List<string>();

        var fiber = ctx.Plugin(new FunctionPlugin("broken", c =>
        {
            c.Effect(new ActionDisposable(() => order.Add("partial-unwound")));
            throw new InvalidOperationException("apply exploded");
        }));
        await fiber.WhenSettledAsync();

        Assert.Equal(FiberState.Failed, fiber.State);
        Assert.Equal("apply exploded", fiber.Error?.Message);
        Assert.Equal(["partial-unwound"], order);
    }

    [Fact]
    public async Task An_individual_effect_can_be_unwound_without_touching_the_rest()
    {
        var ctx = Context.CreateRoot();
        var order = new List<string>();
        IDisposable? single = null;

        var fiber = ctx.Plugin(new FunctionPlugin("selective", c =>
        {
            c.Effect(new ActionDisposable(() => order.Add("kept")));
            single = c.Effect(new ActionDisposable(() => order.Add("removed")));
            return Task.CompletedTask;
        }));
        await fiber.WhenSettledAsync();

        single!.Dispose();
        Assert.Equal(["removed"], order);

        await fiber.DisposeAsync();
        Assert.Equal(["removed", "kept"], order);
    }
}

using Dsh.Cordis;
using Dsh.Llm;
using Dsh.Llm.Fake;

namespace Dsh.Tests.Llm;

public sealed class LlmRuntimeTests
{
    private static async Task<(Context Ctx, LlmRuntime Llm)> RuntimeAsync()
    {
        var ctx = Context.CreateRoot();
        var fiber = ctx.Plugin(LlmRuntime.Plugin());
        await fiber.WhenSettledAsync();
        return (ctx, ctx.Require<LlmRuntime>(LlmKeys.Service));
    }

    private static GenerateOptions Request(string provider = ScriptedAdapter.ProviderRoute)
        => new(
            new LlmCallConfig(provider, ScriptedAdapter.ModelId),
            [Message.UserText("hello")]);

    private static async Task<List<StreamChunk>> DrainAsync(IAsyncEnumerable<StreamChunk> stream)
    {
        var chunks = new List<StreamChunk>();
        await foreach (var chunk in stream) chunks.Add(chunk);
        return chunks;
    }

    [Fact]
    public async Task A_registered_adapter_serves_its_route()
    {
        var (_, llm) = await RuntimeAsync();
        llm.RegisterAdapter([ScriptedAdapter.ProviderRoute], new ScriptedAdapter(ScriptedReply.Text("hi there")));

        var chunks = await DrainAsync(llm.StreamAsync(Request()));

        Assert.IsType<FinishChunk>(chunks[^1]);
        Assert.Contains(chunks, chunk => chunk is TextDeltaChunk);
    }

    [Fact]
    public async Task Withdrawing_a_registration_frees_the_route()
    {
        var (_, llm) = await RuntimeAsync();
        var registration = llm.RegisterAdapter([ScriptedAdapter.ProviderRoute], new ScriptedAdapter());

        Assert.Contains(ScriptedAdapter.ProviderRoute, llm.Providers);
        registration.Dispose();
        Assert.DoesNotContain(ScriptedAdapter.ProviderRoute, llm.Providers);
    }

    [Fact]
    public async Task A_route_cannot_be_served_twice()
    {
        var (_, llm) = await RuntimeAsync();
        llm.RegisterAdapter([ScriptedAdapter.ProviderRoute], new ScriptedAdapter());

        var error = Assert.Throws<LlmError>(
            () => llm.RegisterAdapter([ScriptedAdapter.ProviderRoute], new ScriptedAdapter()));

        Assert.Equal("DUPLICATE_ADAPTER", error.Code);
    }

    [Fact]
    public async Task An_unserved_route_ends_the_stream_with_a_no_adapter_failure()
    {
        var (_, llm) = await RuntimeAsync();

        var chunks = await DrainAsync(llm.StreamAsync(Request("nobody")));

        var finish = Assert.IsType<FinishChunk>(Assert.Single(chunks));
        var error = Assert.IsType<ErrorFinish>(finish.Reason);
        Assert.Equal(LlmErrorCodes.NoAdapter, error.Failure.Code);
    }

    private sealed class ThrowingAdapter : LlmAdapter
    {
        public override IAsyncEnumerable<StreamChunk> StreamAsync(
            GenerateOptions options,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("adapter exploded");
    }

    [Fact]
    public async Task An_adapter_that_throws_becomes_a_terminal_error_finish_rather_than_an_exception()
    {
        var (_, llm) = await RuntimeAsync();
        llm.RegisterAdapter([ScriptedAdapter.ProviderRoute], new ThrowingAdapter());

        var chunks = await DrainAsync(llm.StreamAsync(Request()));

        var finish = Assert.IsType<FinishChunk>(Assert.Single(chunks));
        var error = Assert.IsType<ErrorFinish>(finish.Reason);
        Assert.Equal("adapter exploded", error.Failure.Message);
    }

    [Fact]
    public async Task Preparing_a_call_materializes_the_adapters_own_defaults()
    {
        var (_, llm) = await RuntimeAsync();
        llm.RegisterAdapter([ScriptedAdapter.ProviderRoute], new ScriptedAdapter());

        var prepared = await llm.PrepareCallAsync(
            new LlmCallConfig(ScriptedAdapter.ProviderRoute, ScriptedAdapter.ModelId));

        Assert.Equal(8_192, prepared.Config.MaxTokens);
        Assert.True(prepared.AdapterDefaults.MaxTokens);
        Assert.False(prepared.AdapterDefaults.ReasoningEffort);
        Assert.Equal(200_000, prepared.Context?.ContextWindow);
    }

    [Fact]
    public async Task A_caller_supplied_value_is_not_marked_as_an_adapter_default()
    {
        var (_, llm) = await RuntimeAsync();
        llm.RegisterAdapter([ScriptedAdapter.ProviderRoute], new ScriptedAdapter());

        var prepared = await llm.PrepareCallAsync(
            new LlmCallConfig(ScriptedAdapter.ProviderRoute, ScriptedAdapter.ModelId, MaxTokens: 100));

        Assert.Equal(100, prepared.Config.MaxTokens);
        Assert.False(prepared.AdapterDefaults.MaxTokens);
    }

    [Fact]
    public async Task Preparing_a_call_for_an_unserved_route_fails_loudly()
    {
        var (_, llm) = await RuntimeAsync();

        var error = await Assert.ThrowsAsync<LlmError>(
            () => llm.PrepareCallAsync(new LlmCallConfig("nobody", "model")));

        Assert.Equal(LlmErrorCodes.NoAdapter, error.Code);
    }

    [Fact]
    public async Task A_stream_listener_can_wrap_every_request()
    {
        var (ctx, llm) = await RuntimeAsync();
        llm.RegisterAdapter([ScriptedAdapter.ProviderRoute], new ScriptedAdapter(ScriptedReply.Text("hi")));
        var observed = 0;
        ctx.OnWaterfall(LlmKeys.Stream, (options, next) =>
        {
            observed++;
            return next();
        });

        await DrainAsync(llm.StreamAsync(Request()));

        Assert.Equal(1, observed);
    }

    [Fact]
    public async Task A_stream_listener_can_serve_a_request_without_the_adapter()
    {
        var (ctx, llm) = await RuntimeAsync();
        var adapter = new ScriptedAdapter(ScriptedReply.Text("from the adapter"));
        llm.RegisterAdapter([ScriptedAdapter.ProviderRoute], adapter);
        ctx.OnWaterfall(LlmKeys.Stream, (options, next) => Task.FromResult(Replay()));

        var chunks = await DrainAsync(llm.StreamAsync(Request()));

        Assert.Equal("from the cache", Assert.IsType<TextDeltaChunk>(chunks[0]).Text);
        Assert.Empty(adapter.Requests);

        static async IAsyncEnumerable<StreamChunk> Replay()
        {
            await Task.CompletedTask;
            yield return new TextDeltaChunk(0, "from the cache");
            yield return new FinishChunk(StopFinish.Instance);
        }
    }

    [Fact]
    public async Task Replay_state_is_stripped_before_it_reaches_a_different_adapter()
    {
        var (_, llm) = await RuntimeAsync();
        var adapter = new ScriptedAdapter(ScriptedReply.Text("ok"));
        llm.RegisterAdapter([ScriptedAdapter.ProviderRoute], adapter);

        var history = new List<Message>
        {
            Message.Assistant(
                [new TextBlock("earlier")],
                new ModelMessageSource("some-other-provider", "some-model", "opaque-state")),
        };

        await DrainAsync(llm.StreamAsync(new GenerateOptions(
            new LlmCallConfig(ScriptedAdapter.ProviderRoute, ScriptedAdapter.ModelId),
            history)));

        var sent = Assert.Single(adapter.Requests);
        var source = Assert.IsType<ModelMessageSource>(sent.Messages[0].Source);
        Assert.Null(source.ReplayState);
    }

    [Fact]
    public async Task Replay_state_survives_for_the_adapter_that_produced_it()
    {
        var (_, llm) = await RuntimeAsync();
        var adapter = new ScriptedAdapter(ScriptedReply.Text("ok"));
        llm.RegisterAdapter([ScriptedAdapter.ProviderRoute], adapter);

        var history = new List<Message>
        {
            Message.Assistant(
                [new TextBlock("earlier")],
                new ModelMessageSource(ScriptedAdapter.ProviderRoute, ScriptedAdapter.ModelId, "opaque-state")),
        };

        await DrainAsync(llm.StreamAsync(new GenerateOptions(
            new LlmCallConfig(ScriptedAdapter.ProviderRoute, ScriptedAdapter.ModelId),
            history)));

        var sent = Assert.Single(adapter.Requests);
        var source = Assert.IsType<ModelMessageSource>(sent.Messages[0].Source);
        Assert.Equal("opaque-state", source.ReplayState);
    }
}

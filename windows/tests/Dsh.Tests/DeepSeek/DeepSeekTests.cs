using System.Text;
using System.Text.Json;
using Dsh.Llm;
using Dsh.Llm.DeepSeek;

namespace Dsh.Tests.DeepSeek;

public sealed class SseReaderTests
{
    private static Stream Body(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    private static async Task<List<string>> ReadAsync(string text, Action<string>? onComment = null)
    {
        var payloads = new List<string>();
        await foreach (var payload in SseReader.ReadAsync(Body(text), onComment)) payloads.Add(payload);
        return payloads;
    }

    [Fact]
    public async Task Events_arrive_in_order_and_end_at_the_sentinel()
    {
        var payloads = await ReadAsync("data: one\n\ndata: two\n\ndata: [DONE]\n\n");

        Assert.Equal(["one", "two", SseReader.Done], payloads);
    }

    [Fact]
    public async Task An_event_dispatches_only_on_its_blank_line()
    {
        var payloads = await ReadAsync("data: first\ndata: second\n\ndata: [DONE]\n\n");

        // Two data fields in one event join, rather than becoming two events.
        Assert.Equal(["first\nsecond", SseReader.Done], payloads);
    }

    [Fact]
    public async Task A_stream_that_ends_without_the_sentinel_is_treated_as_truncation()
    {
        var error = await Assert.ThrowsAsync<LlmError>(() => ReadAsync("data: one\n\n"));

        Assert.Equal(LlmErrorCodes.StreamClosed, error.Code);
    }

    [Fact]
    public async Task An_unterminated_final_event_is_truncation_not_a_flushable_payload()
    {
        var error = await Assert.ThrowsAsync<LlmError>(() => ReadAsync("data: one\n\ndata: half"));

        Assert.Equal(LlmErrorCodes.StreamClosed, error.Code);
    }

    [Fact]
    public async Task Comments_prove_the_connection_is_alive_without_becoming_payloads()
    {
        var comments = new List<string>();
        var payloads = await ReadAsync(": keep-alive\n\ndata: one\n\ndata: [DONE]\n\n", comments.Add);

        Assert.Equal(["one", SseReader.Done], payloads);
        Assert.Equal(["keep-alive"], comments);
    }

    [Fact]
    public async Task Fields_other_than_data_are_ignored()
    {
        var payloads = await ReadAsync("event: message\nid: 7\ndata: one\n\ndata: [DONE]\n\n");

        Assert.Equal(["one", SseReader.Done], payloads);
    }
}

public sealed class TranslatorTests
{
    private static WireChunk Chunk(WireDelta? delta, string? finish = null, WireUsage? usage = null)
        => new([new WireChoice(delta, finish)], usage);

    [Fact]
    public void Text_deltas_become_a_block_that_opens_once()
    {
        var translator = new StreamTranslator();

        var first = translator.Push(Chunk(new WireDelta("hello ", null, null))).ToList();
        var second = translator.Push(Chunk(new WireDelta("world", null, null))).ToList();

        Assert.Collection(
            first,
            static chunk => Assert.IsType<BlockStartChunk>(chunk),
            static chunk => Assert.Equal("hello ", Assert.IsType<TextDeltaChunk>(chunk).Text));
        Assert.Equal("world", Assert.IsType<TextDeltaChunk>(Assert.Single(second)).Text);
    }

    [Fact]
    public void The_first_empty_thinking_fragment_does_not_open_a_blank_block()
    {
        var translator = new StreamTranslator();

        var emitted = translator.Push(Chunk(new WireDelta(null, string.Empty, null))).ToList();

        Assert.Empty(emitted);
    }

    [Fact]
    public void Thinking_arrives_before_prose_as_a_separate_block()
    {
        var translator = new StreamTranslator();
        translator.Push(Chunk(new WireDelta(null, "considering", null)));
        translator.Push(Chunk(new WireDelta("answer", null, null)));
        translator.Push(Chunk(new WireDelta(null, null, null), "stop"));

        var blocks = translator.Complete().OfType<BlockEndChunk>().Select(static chunk => chunk.Block).ToList();

        Assert.Equal("considering", Assert.IsType<ReasoningBlock>(blocks[0]).Text);
        Assert.Equal("answer", Assert.IsType<TextBlock>(blocks[1]).Text);
    }

    [Fact]
    public void Tool_call_fragments_collect_by_the_providers_index()
    {
        var translator = new StreamTranslator();
        translator.Push(Chunk(new WireDelta(null, null,
            [new WireToolCallDelta(0, "call-1", new WireToolCallFunctionDelta("read", "{\"file"))])));
        translator.Push(Chunk(new WireDelta(null, null,
            [new WireToolCallDelta(0, null, new WireToolCallFunctionDelta(null, "_path\":\"a\"}"))])));
        translator.Push(Chunk(new WireDelta(null, null, null), "tool_calls"));

        var call = Assert.IsType<ToolCallBlock>(
            translator.Complete().OfType<BlockEndChunk>().Single().Block);

        Assert.Equal("read", call.Name);
        Assert.Equal(new CallId("call-1"), call.Id);
        Assert.Equal("{\"file_path\":\"a\"}", call.Arguments);
    }

    [Fact]
    public void Parallel_tool_calls_stay_separate()
    {
        var translator = new StreamTranslator();
        translator.Push(Chunk(new WireDelta(null, null,
        [
            new WireToolCallDelta(0, "call-1", new WireToolCallFunctionDelta("read", "{}")),
            new WireToolCallDelta(1, "call-2", new WireToolCallFunctionDelta("glob", "{}")),
        ])));
        translator.Push(Chunk(new WireDelta(null, null, null), "tool_calls"));

        var calls = translator.Complete().OfType<BlockEndChunk>()
            .Select(static chunk => Assert.IsType<ToolCallBlock>(chunk.Block)).ToList();

        Assert.Equal(["read", "glob"], calls.Select(static call => call.Name));
    }

    [Fact]
    public void Nothing_terminal_is_emitted_before_the_stream_completes()
    {
        var translator = new StreamTranslator();

        var emitted = translator.Push(Chunk(new WireDelta("hi", null, null), "stop")).ToList();

        Assert.DoesNotContain(emitted, static chunk => chunk is FinishChunk);
    }

    [Fact]
    public void Completion_emits_block_ends_then_usage_then_exactly_one_finish()
    {
        var translator = new StreamTranslator();
        translator.Push(Chunk(new WireDelta("hi", null, null)));
        translator.Push(Chunk(null, "stop", new WireUsage(100, 5)));

        var emitted = translator.Complete().ToList();

        Assert.IsType<BlockEndChunk>(emitted[0]);
        Assert.IsType<UsageChunk>(emitted[1]);
        Assert.IsType<FinishChunk>(emitted[2]);
        Assert.Equal(3, emitted.Count);
    }

    [Fact]
    public void A_completion_with_no_content_becomes_a_retryable_empty_response()
    {
        var translator = new StreamTranslator();
        translator.Push(Chunk(new WireDelta(null, null, null), "stop"));

        var finish = Assert.IsType<FinishChunk>(Assert.Single(translator.Complete()));
        var failure = Assert.IsType<ErrorFinish>(finish.Reason);

        Assert.Equal(LlmErrorCodes.EmptyResponse, failure.Failure.Code);
        Assert.True(new ResolvedRetryPolicy().IsRetryable(failure.Failure.Code));
    }

    [Theory]
    [InlineData("stop", typeof(StopFinish))]
    [InlineData("tool_calls", typeof(ToolCallsFinish))]
    [InlineData("length", typeof(MaxTokensFinish))]
    [InlineData("content_filter", typeof(ErrorFinish))]
    public void Stop_reasons_map_to_the_harness_vocabulary(string reason, Type expected)
    {
        Assert.IsType(expected, StreamTranslator.MapFinish(reason));
    }

    [Fact]
    public void Cache_hits_are_subtracted_so_the_counts_stay_disjoint()
    {
        var usage = StreamTranslator.MapUsage(new WireUsage(1000, 50, PromptCacheHitTokens: 400));

        Assert.Equal(600, usage.InputTokens);
        Assert.Equal(400, usage.CacheReadTokens);
        Assert.Equal(1000, usage.TotalInputTokens);
    }

    [Fact]
    public void The_compatibility_spelling_of_the_cache_count_is_read_too()
    {
        var usage = StreamTranslator.MapUsage(
            new WireUsage(1000, 50, PromptTokensDetails: new WirePromptDetails(250)));

        Assert.Equal(750, usage.InputTokens);
        Assert.Equal(250, usage.CacheReadTokens);
    }
}

public sealed class SerializerTests
{
    private static ModelMessageSource Route => new("deepseek-official", "deepseek-v4-flash");

    [Fact]
    public void A_system_prompt_leads_the_message_list()
    {
        var wire = WireSerializer.Serialize("be brief", [Message.UserText("hi")]);

        Assert.Equal("system", wire[0].Role);
        Assert.Equal("be brief", wire[0].Content);
        Assert.Equal("user", wire[1].Role);
    }

    [Fact]
    public void A_tool_result_becomes_a_tool_message_keyed_by_the_call_it_answers()
    {
        var history = new List<Message>
        {
            Message.ToolResult(new CallId("call-1"), [new TextBlock("contents")], isError: false),
        };

        var wire = WireSerializer.Serialize(null, history);

        var message = Assert.Single(wire);
        Assert.Equal("tool", message.Role);
        Assert.Equal("call-1", message.ToolCallId);
        Assert.Equal("contents", message.Content);
    }

    [Fact]
    public void A_tool_that_produced_nothing_still_answers_its_call()
    {
        var history = new List<Message>
        {
            Message.ToolResult(new CallId("call-1"), [], isError: false),
        };

        var message = Assert.Single(WireSerializer.Serialize(null, history));

        Assert.Equal("(no output)", message.Content);
    }

    [Fact]
    public void A_text_less_assistant_turn_sends_an_empty_string_never_null()
    {
        var history = new List<Message>
        {
            Message.Assistant([new ToolCallBlock(new CallId("call-1"), "read", "{}")], Route),
        };

        var message = Assert.Single(WireSerializer.Serialize(null, history));

        Assert.Equal(string.Empty, message.Content);
        Assert.NotNull(message.ToolCalls);
    }

    [Fact]
    public void Thinking_is_replayed_on_a_turn_that_carried_it()
    {
        var history = new List<Message>
        {
            Message.Assistant([new ReasoningBlock("weighing options"), new TextBlock("done")], Route),
        };

        var message = Assert.Single(WireSerializer.Serialize(null, history));

        Assert.Equal("weighing options", message.ReasoningContent);
        Assert.Equal("done", message.Content);
    }

    [Fact]
    public void A_turn_that_carried_no_thinking_sends_no_thinking_field()
    {
        var history = new List<Message> { Message.Assistant([new TextBlock("done")], Route) };

        Assert.Null(Assert.Single(WireSerializer.Serialize(null, history)).ReasoningContent);
    }

    [Fact]
    public void A_message_with_prose_and_a_tool_result_produces_both_turns()
    {
        var history = new List<Message>
        {
            new(
                MessageId.New(),
                MessageRole.User,
                [new TextBlock("also note this"), new ToolResultBlock(new CallId("call-1"), [new TextBlock("out")])],
                UserMessageSource.Instance),
        };

        var wire = WireSerializer.Serialize(null, history);

        Assert.Equal(["user", "tool"], wire.Select(static message => message.Role));
    }
}

public sealed class DeepSeekAdapterTests
{
    private static DeepSeekAdapter Adapter(DeepSeekConfig? config = null)
        => new(config ?? new DeepSeekConfig(), static () => "sk-test");

    private static GenerateOptions Request(
        ReasoningEffortId? effort = null,
        RequestPurpose purpose = RequestPurpose.Conversation)
        => new(
            new LlmCallConfig("deepseek-official", DeepSeekConfig.DefaultModel, effort),
            [Message.UserText("hi")],
            Purpose: purpose);

    [Fact]
    public void A_request_always_streams_and_always_asks_for_usage()
    {
        using var adapter = Adapter();

        var request = adapter.BuildRequest(Request());

        Assert.True(request.Stream);
        Assert.True(request.StreamOptions.IncludeUsage);
    }

    [Fact]
    public void Naming_a_session_never_spends_thinking_tokens()
    {
        using var adapter = Adapter();

        var (thinking, effort) = adapter.ResolveThinking(
            Request(new ReasoningEffortId("high"), RequestPurpose.SessionTitle));

        Assert.Equal("disabled", thinking);
        Assert.Null(effort);
    }

    [Fact]
    public void The_off_effort_never_reaches_the_wire()
    {
        using var adapter = Adapter();

        var (thinking, effort) = adapter.ResolveThinking(Request(new ReasoningEffortId("off")));

        Assert.Equal("disabled", thinking);
        Assert.Null(effort);
    }

    [Fact]
    public void A_real_effort_enables_thinking_and_is_sent_alongside_it()
    {
        using var adapter = Adapter();

        var (thinking, effort) = adapter.ResolveThinking(Request(new ReasoningEffortId("max")));

        Assert.Equal("enabled", thinking);
        Assert.Equal("max", effort);
    }

    [Fact]
    public void Asking_for_effort_on_a_deployment_that_disabled_thinking_fails_before_any_request()
    {
        using var adapter = Adapter(new DeepSeekConfig(Thinking: false));

        var error = Assert.Throws<LlmError>(() => adapter.ResolveThinking(Request(new ReasoningEffortId("high"))));

        Assert.Equal(LlmErrorCodes.UnsupportedReasoningEffort, error.Code);
    }

    [Theory]
    [InlineData(401, "", LlmErrorCodes.InvalidCredential)]
    [InlineData(429, "", LlmErrorCodes.RateLimit)]
    [InlineData(500, "", LlmErrorCodes.Server)]
    [InlineData(413, "", LlmErrorCodes.InvalidRequest)]
    [InlineData(400, "this model's maximum context length is 8192 tokens", LlmErrorCodes.ContextWindowExceeded)]
    [InlineData(400, "malformed request", LlmErrorCodes.InvalidRequest)]
    [InlineData(402, "insufficient balance", LlmErrorCodes.QuotaExceeded)]
    public void Http_failures_classify_into_codes_policy_can_act_on(int status, string detail, string expected)
    {
        Assert.Equal(expected, DeepSeekAdapter.ClassifyStatus(status, detail));
    }

    [Fact]
    public void A_rate_limit_is_retryable_and_a_bad_request_is_not()
    {
        var policy = new ResolvedRetryPolicy();

        Assert.True(policy.IsRetryable(LlmErrorCodes.RateLimit));
        Assert.False(policy.IsRetryable(LlmErrorCodes.InvalidRequest));
        Assert.False(policy.IsRetryable(LlmErrorCodes.QuotaExceeded));
    }

    [Fact]
    public void Backoff_grows_and_then_saturates_at_the_ceiling()
    {
        var policy = new ResolvedRetryPolicy(InitialDelayMs: 500, MaxDelayMs: 10_000, JitterRatio: 0);

        Assert.Equal(500, policy.DelayFor(1, 0.5));
        Assert.Equal(1000, policy.DelayFor(2, 0.5));
        Assert.Equal(10_000, policy.DelayFor(20, 0.5));
    }

    [Fact]
    public void The_base_url_can_be_overridden_from_the_environment()
    {
        var previous = Environment.GetEnvironmentVariable(DeepSeekConfig.BaseUrlEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(DeepSeekConfig.BaseUrlEnvironmentVariable, "https://proxy.example/v1/");
            Assert.Equal("https://proxy.example/v1", new DeepSeekConfig().ResolveBaseUrl());
        }
        finally
        {
            Environment.SetEnvironmentVariable(DeepSeekConfig.BaseUrlEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void The_public_endpoint_is_used_when_nothing_overrides_it()
    {
        var previous = Environment.GetEnvironmentVariable(DeepSeekConfig.BaseUrlEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(DeepSeekConfig.BaseUrlEnvironmentVariable, null);
            Assert.Equal("https://api.deepseek.com", new DeepSeekConfig().ResolveBaseUrl());
        }
        finally
        {
            Environment.SetEnvironmentVariable(DeepSeekConfig.BaseUrlEnvironmentVariable, previous);
        }
    }

    [Fact]
    public async Task A_model_the_catalog_does_not_list_still_works_as_a_text_route()
    {
        using var adapter = Adapter();

        var resolved = await adapter.ResolveModelAsync("deepseek-official", "some-future-model");

        Assert.Equal("some-future-model", resolved.Info.Id);
        Assert.Equal(1_000_000, resolved.Context?.ContextWindow);
    }

    [Fact]
    public async Task A_request_without_a_key_says_which_name_to_set()
    {
        using var adapter = new DeepSeekAdapter(new DeepSeekConfig(), static () => null);

        var error = await Assert.ThrowsAsync<LlmError>(async () =>
        {
            await foreach (var _ in adapter.StreamAsync(Request(), CancellationToken.None)) { }
        });

        Assert.Equal(LlmErrorCodes.MissingCredential, error.Code);
        Assert.Contains("DEEPSEEK_API_KEY", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_serialized_request_uses_the_providers_own_field_names()
    {
        using var adapter = Adapter();
        var request = adapter.BuildRequest(new GenerateOptions(
            new LlmCallConfig("deepseek-official", "m", MaxTokens: 100),
            [Message.UserText("hi")],
            "be brief",
            [new ToolSchema("read", "read a file", new Dictionary<string, object?> { ["type"] = "object" })]));

        var json = JsonSerializer.Serialize(request);

        Assert.Contains("\"stream_options\"", json, StringComparison.Ordinal);
        Assert.Contains("\"max_tokens\":100", json, StringComparison.Ordinal);
        Assert.Contains("\"tools\"", json, StringComparison.Ordinal);
    }
}

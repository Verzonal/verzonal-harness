using Dsh.Llm;

namespace Dsh.Tests.Llm;

public sealed class BlockAssemblerTests
{
    [Fact]
    public void Deltas_assemble_into_one_text_block()
    {
        var assembler = new BlockAssembler();
        assembler.Push(new BlockStartChunk(0, "text"));
        assembler.Push(new TextDeltaChunk(0, "hello "));
        assembler.Push(new TextDeltaChunk(0, "world"));
        assembler.Push(new FinishChunk(StopFinish.Instance));

        var block = Assert.IsType<TextBlock>(Assert.Single(assembler.Blocks()));

        Assert.Equal("hello world", block.Text);
    }

    [Fact]
    public void A_delta_for_an_unopened_index_opens_the_block_itself()
    {
        var assembler = new BlockAssembler();
        assembler.Push(new TextDeltaChunk(0, "no block-start"));
        assembler.Push(new FinishChunk(StopFinish.Instance));

        var block = Assert.IsType<TextBlock>(Assert.Single(assembler.Blocks()));

        Assert.Equal("no block-start", block.Text);
    }

    [Fact]
    public void Reasoning_and_text_assemble_into_separate_blocks_in_first_seen_order()
    {
        var assembler = new BlockAssembler();
        assembler.Push(new ReasoningDeltaChunk(0, "thinking"));
        assembler.Push(new TextDeltaChunk(1, "answer"));
        assembler.Push(new FinishChunk(StopFinish.Instance));

        var blocks = assembler.Blocks();

        Assert.Equal(2, blocks.Count);
        Assert.Equal("thinking", Assert.IsType<ReasoningBlock>(blocks[0]).Text);
        Assert.Equal("answer", Assert.IsType<TextBlock>(blocks[1]).Text);
    }

    [Fact]
    public void Tool_call_argument_fragments_concatenate_by_index()
    {
        var assembler = new BlockAssembler();
        assembler.Push(new ToolCallDeltaChunk(0, new CallId("call-1"), "read", "{\"file"));
        assembler.Push(new ToolCallDeltaChunk(0, new CallId("call-1"), null, "_path\":\"a.txt\"}"));
        assembler.Push(new FinishChunk(ToolCallsFinish.Instance));

        var call = Assert.IsType<ToolCallBlock>(Assert.Single(assembler.Blocks()));

        Assert.Equal(new CallId("call-1"), call.Id);
        Assert.Equal("read", call.Name);
        Assert.Equal("{\"file_path\":\"a.txt\"}", call.Arguments);
    }

    [Fact]
    public void Parallel_tool_calls_stay_separate_by_index()
    {
        var assembler = new BlockAssembler();
        assembler.Push(new ToolCallDeltaChunk(0, new CallId("call-1"), "read", "{}"));
        assembler.Push(new ToolCallDeltaChunk(1, new CallId("call-2"), "glob", "{}"));
        assembler.Push(new FinishChunk(ToolCallsFinish.Instance));

        var blocks = assembler.Blocks();

        Assert.Equal(2, blocks.Count);
        Assert.Equal("read", Assert.IsType<ToolCallBlock>(blocks[0]).Name);
        Assert.Equal("glob", Assert.IsType<ToolCallBlock>(blocks[1]).Name);
    }

    [Fact]
    public void A_max_tokens_finish_drops_tool_calls_because_a_truncated_call_cannot_be_run()
    {
        var assembler = new BlockAssembler();
        assembler.Push(new TextDeltaChunk(0, "I will read"));
        assembler.Push(new ToolCallDeltaChunk(1, new CallId("call-1"), "read", "{\"file_pa"));
        assembler.Push(new FinishChunk(MaxTokensFinish.Instance));

        var blocks = assembler.Blocks();

        Assert.Single(blocks);
        Assert.IsType<TextBlock>(blocks[0]);
    }

    [Fact]
    public void Interrupted_blocks_keep_only_non_blank_prose()
    {
        var assembler = new BlockAssembler();
        assembler.Push(new TextDeltaChunk(0, "partial answer"));
        assembler.Push(new ReasoningDeltaChunk(1, "   "));
        assembler.Push(new ToolCallDeltaChunk(2, new CallId("call-1"), "read", "{}"));

        var blocks = assembler.InterruptedBlocks();

        Assert.Single(blocks);
        Assert.Equal("partial answer", Assert.IsType<TextBlock>(blocks[0]).Text);
    }

    [Fact]
    public void A_block_end_wins_over_later_deltas_for_the_same_index()
    {
        var assembler = new BlockAssembler();
        assembler.Push(new TextDeltaChunk(0, "first"));
        assembler.Push(new BlockEndChunk(0, new TextBlock("first")));
        assembler.Push(new TextDeltaChunk(0, " ignored"));
        assembler.Push(new FinishChunk(StopFinish.Instance));

        Assert.Equal("first", Assert.IsType<TextBlock>(Assert.Single(assembler.Blocks())).Text);
    }

    [Fact]
    public void Usage_and_finish_are_recorded_for_the_step()
    {
        var assembler = new BlockAssembler();
        assembler.Push(new TextDeltaChunk(0, "hi"));
        assembler.Push(new UsageChunk(new TokenUsage(100, 5, CacheReadTokens: 20)));
        assembler.Push(new FinishChunk(StopFinish.Instance));

        Assert.Equal(100, assembler.Usage?.InputTokens);
        Assert.Equal(120, assembler.Usage?.TotalInputTokens);
        Assert.IsType<StopFinish>(assembler.Finish);
    }
}

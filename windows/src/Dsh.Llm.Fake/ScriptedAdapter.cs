using System.Runtime.CompilerServices;
using Dsh.Cordis;
using Dsh.Llm;

namespace Dsh.Llm.Fake;

/// <summary>
/// One scripted model reply: the chunks it streams, in order.
/// </summary>
/// <param name="Chunks">Everything the turn emits, ending in a terminal finish.</param>
public sealed record ScriptedReply(IReadOnlyList<StreamChunk> Chunks)
{
    /// <summary>
    /// A reply that streams prose and stops.
    /// </summary>
    /// <param name="text">The text to stream, delivered as one delta per word.</param>
    /// <param name="usage">Token accounting to report, when any.</param>
    /// <returns>The scripted reply.</returns>
    public static ScriptedReply Text(string text, TokenUsage? usage = null)
    {
        var chunks = new List<StreamChunk> { new BlockStartChunk(0, "text") };
        foreach (var piece in Split(text)) chunks.Add(new TextDeltaChunk(0, piece));
        chunks.Add(new BlockEndChunk(0, new TextBlock(text)));
        if (usage is not null) chunks.Add(new UsageChunk(usage));
        chunks.Add(new FinishChunk(StopFinish.Instance));
        return new ScriptedReply(chunks);
    }

    /// <summary>
    /// A reply that thinks aloud before answering.
    /// </summary>
    /// <param name="reasoning">The thinking text.</param>
    /// <param name="text">The visible answer.</param>
    /// <returns>The scripted reply.</returns>
    public static ScriptedReply Reasoned(string reasoning, string text)
    {
        var chunks = new List<StreamChunk>
        {
            new BlockStartChunk(0, "reasoning"),
            new ReasoningDeltaChunk(0, reasoning),
            new BlockEndChunk(0, new ReasoningBlock(reasoning)),
            new BlockStartChunk(1, "text"),
        };
        foreach (var piece in Split(text)) chunks.Add(new TextDeltaChunk(1, piece));
        chunks.Add(new BlockEndChunk(1, new TextBlock(text)));
        chunks.Add(new FinishChunk(StopFinish.Instance));
        return new ScriptedReply(chunks);
    }

    /// <summary>
    /// A reply that asks for tools to be run.
    /// </summary>
    /// <param name="calls">The calls to request, in model order.</param>
    /// <param name="preamble">Prose to stream before the calls, when any.</param>
    /// <returns>The scripted reply.</returns>
    public static ScriptedReply ToolCalls(IReadOnlyList<ToolCallBlock> calls, string? preamble = null)
    {
        var chunks = new List<StreamChunk>();
        var index = 0;
        if (!string.IsNullOrEmpty(preamble))
        {
            chunks.Add(new BlockStartChunk(index, "text"));
            chunks.Add(new TextDeltaChunk(index, preamble));
            chunks.Add(new BlockEndChunk(index, new TextBlock(preamble)));
            index++;
        }

        foreach (var call in calls)
        {
            chunks.Add(new BlockStartChunk(index, "tool-call"));
            chunks.Add(new ToolCallDeltaChunk(index, call.Id, call.Name, call.Arguments));
            chunks.Add(new BlockEndChunk(index, call));
            index++;
        }

        chunks.Add(new FinishChunk(ToolCallsFinish.Instance));
        return new ScriptedReply(chunks);
    }

    /// <summary>
    /// A reply that fails.
    /// </summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="code">The failure code, which decides whether policy retries it.</param>
    /// <returns>The scripted reply.</returns>
    public static ScriptedReply Failure(string message, string code)
        => new([new FinishChunk(new ErrorFinish(new LlmFailure(message, code)))]);

    /// <summary>
    /// A reply cut off at the output-token ceiling.
    /// </summary>
    /// <param name="text">The prose delivered before the ceiling.</param>
    /// <returns>The scripted reply.</returns>
    public static ScriptedReply Truncated(string text)
        => new([
            new BlockStartChunk(0, "text"),
            new TextDeltaChunk(0, text),
            new BlockEndChunk(0, new TextBlock(text)),
            new FinishChunk(MaxTokensFinish.Instance),
        ]);

    private static IEnumerable<string> Split(string text)
    {
        if (text.Length == 0)
        {
            yield break;
        }

        var parts = text.Split(' ');
        for (var index = 0; index < parts.Length; index++)
        {
            yield return index == parts.Length - 1 ? parts[index] : parts[index] + " ";
        }
    }
}

/// <summary>
/// A provider that replays scripted replies instead of calling a service.
/// </summary>
/// <remarks>
/// This is what makes the whole assembled harness testable: the loop, tool pipeline,
/// session log, and UI projection all run for real, and only the model's answer is
/// fixed. Every request it receives is recorded, so a test can assert what the model
/// would actually have been sent.
/// </remarks>
public sealed class ScriptedAdapter : LlmAdapter
{
    private readonly Queue<ScriptedReply> _replies = [];
    private readonly List<GenerateOptions> _requests = [];
    private readonly object _gate = new();

    /// <param name="replies">The replies to serve, in order.</param>
    public ScriptedAdapter(params ScriptedReply[] replies)
    {
        foreach (var reply in replies) _replies.Enqueue(reply);
    }

    /// <summary>The provider route this adapter registers under by default.</summary>
    public const string ProviderRoute = "scripted";

    /// <summary>The model id this adapter advertises.</summary>
    public const string ModelId = "scripted-model";

    /// <summary>Every request the adapter was asked to serve, in order.</summary>
    public IReadOnlyList<GenerateOptions> Requests
    {
        get
        {
            lock (_gate) return [.. _requests];
        }
    }

    /// <summary>The delay inserted between chunks, to exercise streaming and cancellation.</summary>
    public TimeSpan ChunkDelay { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Queue one more reply.
    /// </summary>
    /// <param name="reply">The reply to serve after the ones already queued.</param>
    public void Enqueue(ScriptedReply reply)
    {
        lock (_gate) _replies.Enqueue(reply);
    }

    /// <inheritdoc />
    public override Task<LlmResolvedModelInfo> ResolveModelAsync(
        string provider,
        string model,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new LlmResolvedModelInfo(
            new LlmModelInfo(provider, model, model),
            new LlmModelContext(200_000),
            DefaultMaxTokens: 8_192));

    /// <inheritdoc />
    public override async IAsyncEnumerable<StreamChunk> StreamAsync(
        GenerateOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ScriptedReply reply;
        lock (_gate)
        {
            _requests.Add(options);
            reply = _replies.Count > 0
                ? _replies.Dequeue()
                : ScriptedReply.Text("(the script ran out of replies)");
        }

        foreach (var chunk in reply.Chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ChunkDelay > TimeSpan.Zero) await Task.Delay(ChunkDelay, cancellationToken);
            yield return chunk;
        }
    }

    /// <summary>
    /// Mount this adapter on a route.
    /// </summary>
    /// <param name="adapter">The adapter to mount.</param>
    /// <param name="provider">The route key; <see cref="ProviderRoute" /> when omitted.</param>
    /// <returns>The plugin to hand to <see cref="Context.Plugin" />.</returns>
    public static IPlugin Plugin(ScriptedAdapter adapter, string provider = ProviderRoute)
        => new FunctionPlugin(
            "llm-scripted",
            ctx =>
            {
                ctx.Effect(ctx.Require<LlmRuntime>(LlmKeys.Service).RegisterAdapter([provider], adapter));
                return Task.CompletedTask;
            },
            LlmKeys.Service);
}

namespace Dsh.Llm.DeepSeek;

/// <summary>
/// Turns harness history into the provider's message list.
/// </summary>
/// <remarks>
/// This is where the harness's three roles become the provider's four: a user
/// message carrying a tool result is expanded into a separate <c>tool</c> message
/// keyed by the call it answers.
/// </remarks>
public static class WireSerializer
{
    /// <summary>
    /// Build the message list for one request.
    /// </summary>
    /// <param name="system">The rendered system prompt, when there is one.</param>
    /// <param name="messages">The derived model history.</param>
    /// <returns>The provider's message list.</returns>
    public static IReadOnlyList<WireMessage> Serialize(string? system, IReadOnlyList<Message> messages)
    {
        var wire = new List<WireMessage>();
        if (!string.IsNullOrEmpty(system)) wire.Add(new WireMessage("system", system));

        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case MessageRole.System:
                    wire.Add(new WireMessage("system", ContentBlocks.FlattenText(message.Content)));
                    break;

                case MessageRole.Assistant:
                    wire.Add(SerializeAssistant(message));
                    break;

                default:
                    SerializeUser(message, wire);
                    break;
            }
        }

        return wire;
    }

    private static void SerializeUser(Message message, List<WireMessage> wire)
    {
        var results = message.Content.OfType<ToolResultBlock>().ToArray();
        var text = ContentBlocks.FlattenText(message.Content);

        // A message that carries only tool results contributes no user turn; one that
        // carries prose contributes a user turn even when it also carries results.
        if (text.Length > 0 || results.Length == 0) wire.Add(new WireMessage("user", text));

        foreach (var result in results)
        {
            var content = ContentBlocks.FlattenText(result.Content);

            // A tool that produced nothing still needs some content on the wire: the
            // API rejects an empty tool message, and dropping it would leave the call
            // unanswered.
            wire.Add(new WireMessage(
                "tool",
                content.Length > 0 ? content : "(no output)",
                ToolCallId: result.ToolCallId.Value));
        }
    }

    private static WireMessage SerializeAssistant(Message message)
    {
        var reasoning = ContentBlocks.FlattenReasoning(message.Content);
        var calls = ContentBlocks.ToolCalls(message.Content);

        return new WireMessage(
            "assistant",

            // Empty string, never null. The API rejects a null-content assistant turn,
            // and because the turn is durable in the session log a null would break
            // every later turn of that session, not just this request.
            ContentBlocks.FlattenText(message.Content),
            reasoning.Length > 0 ? reasoning : null,
            calls.Count == 0
                ? null
                : [.. calls.Select(static call => new WireToolCall(
                    call.Id.Value,
                    "function",
                    new WireToolCallFunction(call.Name, call.Arguments)))]);
    }

    /// <summary>
    /// Build the tool list for one request.
    /// </summary>
    /// <param name="tools">The assembled schemas.</param>
    /// <returns>The provider's tool list, or null when there are none.</returns>
    public static IReadOnlyList<WireTool>? SerializeTools(IReadOnlyList<ToolSchema>? tools)
        => tools is not { Count: > 0 }
            ? null
            : [.. tools.Select(static tool => new WireTool(
                "function",
                new WireFunction(tool.Name, tool.Description, tool.Parameters)))];
}

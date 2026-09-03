using Dsh.Agent;
using Dsh.Cordis;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Tools;

namespace Dsh.Tools.Todo;

/// <summary>
/// Records the agent's checklist.
/// </summary>
/// <remarks>
/// The list is replaced whole on every write. Partial updates would need stable ids
/// and would let the model's view and the person's drift apart; replacing the whole
/// list means what the model last said is exactly what the person sees.
/// </remarks>
public sealed class TodoWriteTool : ToolBase
{
    private readonly bool _allowParallelInProgress;

    /// <param name="allowParallelInProgress">
    /// Whether several entries may be in progress at once. A deployment that runs work
    /// in parallel wants this; one that does not gets a check against the model
    /// claiming to do everything simultaneously.
    /// </param>
    public TodoWriteTool(bool allowParallelInProgress = true)
        => _allowParallelInProgress = allowParallelInProgress;

    /// <inheritdoc />
    public override string Name => "todo_write";

    /// <inheritdoc />
    public override string Description =>
        "Record the complete task list, replacing any previous one.";

    /// <inheritdoc />
    public override JsonSchemaNode Parameters { get; } = Schema.Object(
        new Schema.Property(
            "todos",
            Schema.Array(
                Schema.Object(
                    new Schema.Property(
                        "content",
                        Schema.String("What the task is — a short imperative line."),
                        Required: true),
                    new Schema.Property(
                        "status",
                        Schema.String(
                            "pending (not started) | in_progress (now) | completed (done).",
                            ["pending", "in_progress", "completed"]),
                        Required: true)),
                "The COMPLETE task list, replacing any previous list."),
            Required: true));

    /// <inheritdoc />
    public override ToolOutput Output { get; } = new(
        Schema.Object(
            new Schema.Property("count", Schema.Number("How many entries the list holds."), Required: true),
            new Schema.Property("completed", Schema.Number("How many are done."), Required: true),
            new Schema.Property("inProgress", Schema.Number("How many are being worked on."), Required: true),
            new Schema.Property("pending", Schema.Number("How many are not started."), Required: true)),
        Render);

    /// <inheritdoc />
    public override ToolCallView? PresentCall(JsonValue args)
    {
        var count = (args as JsonObject)?.Get("todos") is JsonArray todos ? todos.Items.Count : 0;
        return new GenericCallView($"Update the task list ({count} items)");
    }

    /// <inheritdoc />
    public override Task<JsonValue> ExecuteAsync(JsonValue args, ToolRunContext exec)
    {
        var todos = ParseTodos(args);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var todo in todos)
        {
            if (!seen.Add(todo.Content))
            {
                throw new HarnessError($"the task \"{todo.Content}\" appears more than once", "TODO_DUPLICATE");
            }
        }

        var inProgress = todos.Count(static todo => todo.Status == TodoStatus.InProgress);
        if (!_allowParallelInProgress && inProgress > 1)
        {
            throw new HarnessError(
                $"{inProgress} tasks are marked in_progress; this deployment expects one at a time",
                "TODO_PARALLEL");
        }

        // The list is durable state, so it goes to the log rather than being held
        // anywhere this tool owns: a reload rebuilds it with everything else.
        AgentRegistry.RequireInitiator().Session.Append(SessionEvents.TodoWrite, new TodoWriteData(todos));

        return Task.FromResult(JsonValue.From(new Dictionary<string, object?>
        {
            ["count"] = todos.Count,
            ["completed"] = todos.Count(static todo => todo.Status == TodoStatus.Completed),
            ["inProgress"] = inProgress,
            ["pending"] = todos.Count(static todo => todo.Status == TodoStatus.Pending),
        }));
    }

    private static List<TodoItem> ParseTodos(JsonValue args)
    {
        var todos = new List<TodoItem>();
        if ((args as JsonObject)?.Get("todos") is not JsonArray array) return todos;

        foreach (var entry in array.Items)
        {
            if (entry is not JsonObject item) continue;
            var content = (item.Get("content") as JsonString)?.Value;
            var status = (item.Get("status") as JsonString)?.Value;
            if (content is null || status is null) continue;

            todos.Add(new TodoItem(content, status switch
            {
                "in_progress" => TodoStatus.InProgress,
                "completed" => TodoStatus.Completed,
                _ => TodoStatus.Pending,
            }));
        }

        return todos;
    }

    private static IReadOnlyList<ContentBlock> Render(JsonValue args, JsonValue value)
    {
        var map = (JsonObject)value;
        int Count(string key) => (int)((map.Get(key) as JsonNumber)?.Value ?? 0);

        var parts = new List<string>();
        if (Count("completed") > 0) parts.Add($"{Count("completed")} completed");
        if (Count("inProgress") > 0) parts.Add($"{Count("inProgress")} in progress");
        if (Count("pending") > 0) parts.Add($"{Count("pending")} pending");

        var summary = parts.Count == 0 ? "the list is empty" : string.Join(" · ", parts);
        return [new TextBlock($"Task list updated: {summary}")];
    }
}

/// <summary>Mounts the checklist tool.</summary>
public static class TodoTools
{
    /// <summary>
    /// Mount <c>todo_write</c>.
    /// </summary>
    /// <param name="allowParallelInProgress">Whether several entries may be in progress at once.</param>
    /// <returns>The plugin to hand to <see cref="Context.Plugin" />.</returns>
    public static IPlugin Plugin(bool allowParallelInProgress = true)
        => new FunctionPlugin(
            "tool-todo",
            ctx =>
            {
                ctx.Require<ToolRuntime>(ToolKeys.Service)
                    .Register(ctx, new TodoWriteTool(allowParallelInProgress));
                return Task.CompletedTask;
            },
            ToolKeys.Service);
}

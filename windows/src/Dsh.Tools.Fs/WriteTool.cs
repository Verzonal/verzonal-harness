using Dsh.Fs;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Tools;
using Dsh.Util;

namespace Dsh.Tools.Fs;

/// <summary>Shared helpers for the tools that change files.</summary>
internal static class FileChange
{
    /// <summary>
    /// How much prior content is kept so a UI can draw the diff.
    /// </summary>
    /// <remarks>
    /// Past this the diff card falls back to a summary. The point of the payload is to
    /// let a person see what changed, and a megabyte of prior text serves nobody while
    /// making every replay of the session heavier.
    /// </remarks>
    public const int MaxRetainedText = 200_000;

    /// <summary>The escalation properties a confining deployment adds to a changing tool.</summary>
    public static IReadOnlyList<Schema.Property> EscalationProperties(string action) =>
    [
        new Schema.Property(
            "sandbox_permissions",
            Schema.String(
                $"The wider sandbox mode this {action} needs. Only valid as a one-shot retry of an operation the sandbox just denied; requires justification and user approval.",
                ["workspace-write", "danger-full-access"])),
        new Schema.Property(
            "justification",
            Schema.String(
                $"Required with sandbox_permissions: one sentence for the user explaining why this exact {action} needs the wider access.")),
    ];

    /// <summary>
    /// Build the durable payload a diff card is drawn from.
    /// </summary>
    /// <param name="path">The changed file.</param>
    /// <param name="previous">Its content before the change, or null when it was created.</param>
    /// <param name="updated">Its content after the change.</param>
    /// <returns>The payload, with oversized text left out.</returns>
    public static Dictionary<string, object?> DiffPayload(string path, string? previous, string updated)
    {
        var retainable = previous is null || previous.Length <= MaxRetainedText;
        retainable &= updated.Length <= MaxRetainedText;

        return new Dictionary<string, object?>
        {
            ["path"] = path,
            ["created"] = previous is null,
            ["previousText"] = retainable ? previous : null,
            ["newText"] = retainable ? updated : null,
            ["diff"] = TextDiff.Render(previous, updated),
        };
    }

    /// <summary>
    /// Turn a stored diff payload into the card a UI draws.
    /// </summary>
    /// <param name="meta">The payload from the result event.</param>
    /// <param name="title">The card's header.</param>
    /// <returns>The diff view, or null when the payload cannot support one.</returns>
    public static ToolResultView? DiffView(JsonValue? meta, string title)
    {
        if (meta is not JsonObject map) return null;
        var path = (map.Get("path") as JsonString)?.Value;
        if (path is null) return null;

        var newText = (map.Get("newText") as JsonString)?.Value;
        if (newText is null)
        {
            var diff = (map.Get("diff") as JsonString)?.Value;
            return diff is null ? null : new GenericResultView(title, [new TextBlock(diff)]);
        }

        var previous = (map.Get("previousText") as JsonString)?.Value;
        return new DiffResultView([new FileDiff(path, previous, newText)], title);
    }
}

/// <summary>Creates a file, or replaces one outright.</summary>
public sealed class WriteTool : ToolBase
{
    private readonly FileSystemService _fs;

    /// <param name="fs">The filesystem this tool writes through.</param>
    /// <param name="confined">
    /// Whether a confining backend is mounted, which is what adds the escalation
    /// properties to the schema the model sees.
    /// </param>
    public WriteTool(FileSystemService fs, bool confined = false)
    {
        _fs = fs;
        Schema.Property[] properties =
        [
            new Schema.Property("file_path", Schema.String("Path to write, resolved by the filesystem backend."), Required: true),
            new Schema.Property("content", Schema.String("Full UTF-8 text content to write."), Required: true),
            .. confined ? FileChange.EscalationProperties("write") : [],
        ];
        Parameters = Schema.Object(properties);
    }

    /// <inheritdoc />
    public override string Name => "write";

    /// <inheritdoc />
    public override string Description => "Create or fully replace a UTF-8 text file.";

    /// <inheritdoc />
    public override JsonSchemaNode Parameters { get; }

    /// <inheritdoc />
    public override ToolOutput Output { get; } = new(
        Schema.Object(
            new Schema.Property("path", Schema.String("The file that was written."), Required: true),
            new Schema.Property("created", Schema.Boolean("Whether the file did not exist before."), Required: true),
            new Schema.Property("bytes", Schema.Number("How many bytes were written."), Required: true),
            new Schema.Property("previousText", Schema.String("Prior content, when small enough to keep.")),
            new Schema.Property("newText", Schema.String("New content, when small enough to keep.")),
            new Schema.Property("diff", Schema.String("The change, as a unified diff."), Required: true)),
        Render,
        static (_, value) => value);

    /// <inheritdoc />
    public override ToolCallView? PresentCall(JsonValue args)
    {
        var path = StringArg(args, "file_path");
        var content = StringArg(args, "content");
        if (path is null || content is null) return null;

        // A call-time presenter has not read the file, so it cannot claim to know what
        // was there: the prior side stays null until the result arrives.
        return new DiffCallView(
            $"Write {Path.GetFileName(path)}",
            [new FileDiff(path, null, content)],
            [new FileLocation(path)]);
    }

    /// <inheritdoc />
    public override ToolResultView? PresentResult(JsonValue args, ToolResult result)
    {
        if (result.IsError) return null;
        var path = StringArg(args, "file_path") ?? string.Empty;
        return FileChange.DiffView(result.Meta, $"Write {Path.GetFileName(path)}");
    }

    /// <inheritdoc />
    public override async Task<JsonValue> ExecuteAsync(JsonValue args, ToolRunContext exec)
    {
        var path = StringArg(args, "file_path")!;
        var content = StringArg(args, "content")!;
        var full = _fs.Resolve(path);

        string? previous = null;
        if (_fs.Exists(path))
        {
            try
            {
                previous = await _fs.ReadTextAsync(path, exec.CancellationToken);
            }
            catch (FileSystemException)
            {
                // A file that cannot be read as text still gets replaced; the diff just
                // shows a creation instead of a change.
            }
        }

        await _fs.WriteTextAsync(path, content, exec.CancellationToken);

        var payload = FileChange.DiffPayload(full, previous, content);
        payload["bytes"] = System.Text.Encoding.UTF8.GetByteCount(content);
        return JsonValue.From(payload);
    }

    private static IReadOnlyList<ContentBlock> Render(JsonValue args, JsonValue value)
    {
        var map = (JsonObject)value;
        var path = (map.Get("path") as JsonString)?.Value ?? string.Empty;
        var created = (map.Get("created") as JsonBool)?.Value == true;
        var bytes = (int)((map.Get("bytes") as JsonNumber)?.Value ?? 0);
        var verb = created ? "Created" : "Updated";
        return [new TextBlock($"{verb} {path} ({bytes} bytes)")];
    }
}

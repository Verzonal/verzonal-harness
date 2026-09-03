using Dsh.Fs;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Tools;

namespace Dsh.Tools.Fs;

/// <summary>
/// Replaces literal text in an existing file.
/// </summary>
/// <remarks>
/// The replaced text must be unique unless the caller says otherwise. A silent
/// replace-the-first-match would let a model believe it changed one thing while
/// changing something else entirely, so an ambiguous edit is refused with a count
/// the model can act on.
/// </remarks>
public sealed class EditTool : ToolBase
{
    private readonly FileSystemService _fs;

    /// <param name="fs">The filesystem this tool edits through.</param>
    /// <param name="confined">Whether a confining backend adds the escalation properties.</param>
    public EditTool(FileSystemService fs, bool confined = false)
    {
        _fs = fs;
        Schema.Property[] properties =
        [
            new Schema.Property("file_path", Schema.String("Path to edit, resolved by the filesystem backend."), Required: true),
            new Schema.Property("old_string", Schema.String("Literal text to replace. Must match exactly."), Required: true),
            new Schema.Property(
                "new_string",
                Schema.String("Literal replacement text. Use an empty string to delete the match."),
                Required: true),
            new Schema.Property(
                "replace_all",
                Schema.Boolean("Replace all matches. Defaults to false; when false, old_string must appear exactly once.")),
            .. confined ? FileChange.EscalationProperties("edit") : [],
        ];
        Parameters = Schema.Object(properties);
    }

    /// <inheritdoc />
    public override string Name => "edit";

    /// <inheritdoc />
    public override string Description => "Edit an existing UTF-8 text file by replacing literal text.";

    /// <inheritdoc />
    public override JsonSchemaNode Parameters { get; }

    /// <inheritdoc />
    public override ToolOutput Output { get; } = new(
        Schema.Object(
            new Schema.Property("path", Schema.String("The file that was edited."), Required: true),
            new Schema.Property("replacements", Schema.Number("How many matches were replaced."), Required: true),
            new Schema.Property("created", Schema.Boolean("Always false; edit requires an existing file."), Required: true),
            new Schema.Property("previousText", Schema.String("Prior content, when small enough to keep.")),
            new Schema.Property("newText", Schema.String("New content, when small enough to keep.")),
            new Schema.Property("diff", Schema.String("The change, as a unified diff."), Required: true)),
        Render,
        static (_, value) => value);

    /// <inheritdoc />
    public override ToolCallView? PresentCall(JsonValue args)
    {
        var path = StringArg(args, "file_path");
        var oldText = StringArg(args, "old_string");
        var newText = StringArg(args, "new_string");
        if (path is null || oldText is null || newText is null) return null;

        // The call-time diff is the replacement the model asked for, which is exactly
        // what a reviewer wants to see before it is applied.
        return new DiffCallView(
            $"Edit {Path.GetFileName(path)}",
            [new FileDiff(path, oldText, newText)],
            [new FileLocation(path)]);
    }

    /// <inheritdoc />
    public override ToolResultView? PresentResult(JsonValue args, ToolResult result)
    {
        if (result.IsError) return null;
        var path = StringArg(args, "file_path") ?? string.Empty;
        return FileChange.DiffView(result.Meta, $"Edit {Path.GetFileName(path)}");
    }

    /// <inheritdoc />
    public override async Task<JsonValue> ExecuteAsync(JsonValue args, ToolRunContext exec)
    {
        var path = StringArg(args, "file_path")!;
        var oldText = StringArg(args, "old_string")!;
        var newText = StringArg(args, "new_string")!;
        var replaceAll = BoolArg(args, "replace_all") ?? false;

        var previous = await _fs.ReadTextAsync(path, exec.CancellationToken);

        if (oldText.Length == 0)
        {
            throw new ToolEditException("old_string is empty; give the exact text to replace");
        }

        var occurrences = CountOccurrences(previous, oldText);
        if (occurrences == 0)
        {
            throw new ToolEditException($"old_string was not found in {path}");
        }

        if (occurrences > 1 && !replaceAll)
        {
            throw new ToolEditException(
                $"old_string appears {occurrences} times in {path}; make it unique or pass replace_all");
        }

        var updated = replaceAll
            ? previous.Replace(oldText, newText, StringComparison.Ordinal)
            : ReplaceFirst(previous, oldText, newText);

        await _fs.WriteTextAsync(path, updated, exec.CancellationToken);

        var payload = FileChange.DiffPayload(_fs.Resolve(path), previous, updated);
        payload["replacements"] = replaceAll ? occurrences : 1;
        payload["created"] = false;
        return JsonValue.From(payload);
    }

    private static IReadOnlyList<ContentBlock> Render(JsonValue args, JsonValue value)
    {
        var map = (JsonObject)value;
        var path = (map.Get("path") as JsonString)?.Value ?? string.Empty;
        var replacements = (int)((map.Get("replacements") as JsonNumber)?.Value ?? 0);
        var diff = (map.Get("diff") as JsonString)?.Value ?? string.Empty;
        var plural = replacements == 1 ? "replacement" : "replacements";

        return diff.Length == 0
            ? [new TextBlock($"Edited {path} ({replacements} {plural})")]
            : [new TextBlock($"Edited {path} ({replacements} {plural})\n\n{diff}")];
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string ReplaceFirst(string haystack, string needle, string replacement)
    {
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        return index < 0 ? haystack : haystack[..index] + replacement + haystack[(index + needle.Length)..];
    }
}

/// <summary>An edit the file's content does not support.</summary>
public sealed class ToolEditException : HarnessError
{
    /// <param name="message">What was wrong, written so the model can correct it.</param>
    public ToolEditException(string message) : base(message, "EDIT_NOT_APPLICABLE") { }
}

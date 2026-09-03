using System.Text;
using Dsh.Fs;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Tools;

namespace Dsh.Tools.Fs;

/// <summary>
/// Reads a text file and hands the model line-numbered content.
/// </summary>
/// <remarks>
/// The numbers are not decoration: every later edit is expressed against them, so a
/// read that dropped them would leave the model unable to say precisely where to
/// change something.
/// </remarks>
public sealed class ReadTool : ToolBase
{
    private const int DefaultLimit = 2000;

    private readonly FileSystemService _fs;

    /// <param name="fs">The filesystem this tool reads through.</param>
    public ReadTool(FileSystemService fs) => _fs = fs;

    /// <inheritdoc />
    public override string Name => "read";

    /// <inheritdoc />
    public override string Description =>
        "Read a UTF-8 text file and return line-numbered content.";

    /// <inheritdoc />
    public override JsonSchemaNode Parameters { get; } = Schema.Object(
        new Schema.Property("file_path", Schema.String("Path to read, resolved by the filesystem backend."), Required: true),
        new Schema.Property("offset", Schema.Number("1-based first line to return. Defaults to 1.")),
        new Schema.Property("limit", Schema.Number($"Maximum number of lines to return. Defaults to {DefaultLimit}.")));

    /// <inheritdoc />
    public override ToolOutput Output { get; } = new(
        Schema.Object(
            new Schema.Property("path", Schema.String("The file that was read."), Required: true),
            new Schema.Property("offset", Schema.Number("The 1-based first line returned."), Required: true),
            new Schema.Property("totalLines", Schema.Number("How many lines the file has."), Required: true),
            new Schema.Property("truncated", Schema.Boolean("Whether more lines follow."), Required: true),
            new Schema.Property(
                "lines",
                Schema.Array(
                    Schema.Object(
                        new Schema.Property("number", Schema.Number("1-based line number."), Required: true),
                        new Schema.Property("text", Schema.String("The line's text."), Required: true)),
                    "The returned lines."),
                Required: true)),
        Render,
        ProjectMeta);

    /// <inheritdoc />
    public override bool IsConcurrencySafe(JsonValue args) => true;

    /// <inheritdoc />
    public override ToolCallView? PresentCall(JsonValue args)
    {
        var path = StringArg(args, "file_path");
        if (path is null) return null;
        var line = NumberArg(args, "offset") is { } offset ? (int)offset : (int?)null;
        return new GenericCallView(
            $"Read {Path.GetFileName(path)}",
            ToolCallKind.Read,
            path,
            Locations: [new FileLocation(path, line)]);
    }

    /// <inheritdoc />
    public override ToolResultView? PresentResult(JsonValue args, ToolResult result)
    {
        if (result.IsError || result.Meta is not JsonObject meta) return null;

        var path = (meta.Get("path") as JsonString)?.Value ?? StringArg(args, "file_path") ?? string.Empty;
        var offset = (int)((meta.Get("offset") as JsonNumber)?.Value ?? 1);
        var total = (int)((meta.Get("totalLines") as JsonNumber)?.Value ?? 0);

        var lines = new List<ReadLine>();
        if (meta.Get("lines") is JsonArray array)
        {
            foreach (var entry in array.Items)
            {
                if (entry is not JsonObject line) continue;
                lines.Add(new ReadLine(
                    (int)((line.Get("number") as JsonNumber)?.Value ?? 0),
                    (line.Get("text") as JsonString)?.Value ?? string.Empty));
            }
        }

        return new ReadResultView(path, offset, lines, total, LanguageOf(path));
    }

    /// <inheritdoc />
    public override async Task<JsonValue> ExecuteAsync(JsonValue args, ToolRunContext exec)
    {
        var path = StringArg(args, "file_path")!;
        var offset = Math.Max(1, (int)(NumberArg(args, "offset") ?? 1));
        var limit = Math.Max(1, (int)(NumberArg(args, "limit") ?? DefaultLimit));

        var text = await _fs.ReadTextAsync(path, exec.CancellationToken);
        var all = Dsh.Util.TextDiff.SplitLines(text);

        var lines = new List<JsonValue>();
        for (var index = offset - 1; index < all.Count && lines.Count < limit; index++)
        {
            lines.Add(JsonValue.From(new Dictionary<string, object?>
            {
                ["number"] = index + 1,
                ["text"] = all[index],
            }));
        }

        return JsonValue.From(new Dictionary<string, object?>
        {
            ["path"] = _fs.Resolve(path),
            ["offset"] = offset,
            ["totalLines"] = all.Count,
            ["truncated"] = offset - 1 + lines.Count < all.Count,
            ["lines"] = lines,
        });
    }

    private static IReadOnlyList<ContentBlock> Render(JsonValue args, JsonValue value)
    {
        var map = (JsonObject)value;
        var builder = new StringBuilder();

        if (map.Get("lines") is JsonArray lines)
        {
            if (lines.Items.Count == 0) builder.AppendLine("(the file is empty)");
            foreach (var entry in lines.Items)
            {
                if (entry is not JsonObject line) continue;
                var number = (int)((line.Get("number") as JsonNumber)?.Value ?? 0);
                var text = (line.Get("text") as JsonString)?.Value ?? string.Empty;
                builder.Append(number.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(6))
                    .Append('\t')
                    .AppendLine(text);
            }
        }

        if ((map.Get("truncated") as JsonBool)?.Value == true)
        {
            var total = (int)((map.Get("totalLines") as JsonNumber)?.Value ?? 0);
            builder.AppendLine($"[the file has {total} lines; read further with a larger offset]");
        }

        return [new TextBlock(builder.ToString().TrimEnd('\n'))];
    }

    private static JsonValue ProjectMeta(JsonValue args, JsonValue value) => value;

    /// <summary>
    /// Guess a syntax-highlighting hint from a file's extension.
    /// </summary>
    /// <param name="path">The file's path.</param>
    /// <returns>A language name, or null when the extension says nothing useful.</returns>
    internal static string? LanguageOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".ts" or ".tsx" => "typescript",
        ".js" or ".jsx" => "javascript",
        ".py" => "python",
        ".json" => "json",
        ".yml" or ".yaml" => "yaml",
        ".xml" or ".xaml" or ".csproj" => "xml",
        ".md" => "markdown",
        ".sh" => "bash",
        ".ps1" => "powershell",
        ".html" => "html",
        ".css" => "css",
        ".sql" => "sql",
        ".rs" => "rust",
        ".go" => "go",
        _ => null,
    };
}

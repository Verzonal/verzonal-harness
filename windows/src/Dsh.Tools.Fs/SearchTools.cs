using System.Text;
using System.Text.RegularExpressions;
using Dsh.Cordis;
using Dsh.Fs;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Tools;

namespace Dsh.Tools.Fs;

/// <summary>
/// Finds files by path pattern.
/// </summary>
/// <remarks>
/// Results are newest first and capped, because a model that receives ten thousand
/// paths learns less than one that receives the hundred most recently touched.
/// </remarks>
public sealed class GlobTool : ToolBase
{
    private const int Limit = 100;

    private readonly FileSystemService _fs;

    /// <param name="fs">The filesystem this tool searches.</param>
    public GlobTool(FileSystemService fs) => _fs = fs;

    /// <inheritdoc />
    public override string Name => "glob";

    /// <inheritdoc />
    public override string Description => "Find files whose paths match a glob pattern.";

    /// <inheritdoc />
    public override JsonSchemaNode Parameters { get; } = Schema.Object(
        new Schema.Property(
            "pattern",
            Schema.String(
                "Glob pattern to match file paths against (e.g. \"**/*.cs\", \"src/**/*.test.js\"). A pattern with no \"/\" matches the basename at any depth, so \"*\" and \"*.cs\" both search the whole tree; include a separator to anchor the depth."),
            Required: true),
        new Schema.Property(
            "path",
            Schema.String("Directory to search in. Defaults to the session workspace; a relative path resolves against it.")));

    /// <inheritdoc />
    public override ToolOutput Output { get; } = new(
        Schema.Object(
            new Schema.Property("paths", Schema.Array(Schema.String("A matching file."), "The matches."), Required: true),
            new Schema.Property("total", Schema.Number("How many matched before the cap."), Required: true),
            new Schema.Property("truncated", Schema.Boolean("Whether the list is capped."), Required: true)),
        Render,
        static (_, value) => value);

    /// <inheritdoc />
    public override bool IsConcurrencySafe(JsonValue args) => true;

    /// <inheritdoc />
    public override ToolCallView? PresentCall(JsonValue args)
    {
        var pattern = StringArg(args, "pattern");
        return pattern is null ? null : new GenericCallView($"Find {pattern}", ToolCallKind.Search, pattern);
    }

    /// <inheritdoc />
    public override ToolResultView? PresentResult(JsonValue args, ToolResult result)
    {
        if (result.IsError || result.Meta is not JsonObject map) return null;

        var paths = new List<string>();
        if (map.Get("paths") is JsonArray array)
        {
            foreach (var entry in array.Items)
            {
                if (entry is JsonString path) paths.Add(path.Value);
            }
        }

        return new SearchResultView(
            null,
            paths,
            (int)((map.Get("total") as JsonNumber)?.Value ?? paths.Count),
            (map.Get("truncated") as JsonBool)?.Value == true,
            $"Find {StringArg(args, "pattern")}");
    }

    /// <inheritdoc />
    public override Task<JsonValue> ExecuteAsync(JsonValue args, ToolRunContext exec)
    {
        var pattern = StringArg(args, "pattern")!;
        var root = StringArg(args, "path") ?? ".";
        var (files, truncated) = _fs.Glob(root, pattern, Limit, exec.CancellationToken);

        return Task.FromResult(JsonValue.From(new Dictionary<string, object?>
        {
            ["paths"] = files.Select(static file => file.Path).ToArray(),
            ["total"] = files.Count,
            ["truncated"] = truncated,
        }));
    }

    private static IReadOnlyList<ContentBlock> Render(JsonValue args, JsonValue value)
    {
        var map = (JsonObject)value;
        var paths = map.Get("paths") as JsonArray;
        if (paths is null || paths.Items.Count == 0) return [new TextBlock("No files matched.")];

        var builder = new StringBuilder();
        foreach (var entry in paths.Items)
        {
            if (entry is JsonString path) builder.AppendLine(path.Value);
        }

        if ((map.Get("truncated") as JsonBool)?.Value == true)
        {
            builder.AppendLine($"[showing the {paths.Items.Count} most recently modified matches]");
        }

        return [new TextBlock(builder.ToString().TrimEnd('\n'))];
    }
}

/// <summary>Searches file contents for a regular expression.</summary>
public sealed class GrepTool : ToolBase
{
    private const int MatchLimit = 250;

    private readonly FileSystemService _fs;

    /// <param name="fs">The filesystem this tool searches.</param>
    public GrepTool(FileSystemService fs) => _fs = fs;

    /// <inheritdoc />
    public override string Name => "grep";

    /// <inheritdoc />
    public override string Description => "Search file contents for a regular expression.";

    /// <inheritdoc />
    public override JsonSchemaNode Parameters { get; } = Schema.Object(
        new Schema.Property("pattern", Schema.String("Regular expression to search for."), Required: true),
        new Schema.Property(
            "path",
            Schema.String("File or directory to search. Defaults to the session workspace; a relative path resolves against it.")),
        new Schema.Property(
            "include",
            Schema.String("One glob filter for which files to search (e.g. \"*.cs\", \"*.{js,jsx}\"). Not a list; negation is not supported.")));

    /// <inheritdoc />
    public override ToolOutput Output { get; } = new(
        Schema.Object(
            new Schema.Property(
                "files",
                Schema.Array(
                    Schema.Object(
                        new Schema.Property("path", Schema.String("The matching file."), Required: true),
                        new Schema.Property(
                            "matches",
                            Schema.Array(
                                Schema.Object(
                                    new Schema.Property("lineNumber", Schema.Number("1-based line number."), Required: true),
                                    new Schema.Property("line", Schema.String("The matching line."), Required: true)),
                                "The matching lines."),
                            Required: true)),
                    "One entry per file with matches."),
                Required: true),
            new Schema.Property("total", Schema.Number("How many matching lines were found."), Required: true),
            new Schema.Property("truncated", Schema.Boolean("Whether the matches are capped."), Required: true)),
        Render,
        static (_, value) => value);

    /// <inheritdoc />
    public override bool IsConcurrencySafe(JsonValue args) => true;

    /// <inheritdoc />
    public override ToolCallView? PresentCall(JsonValue args)
    {
        var pattern = StringArg(args, "pattern");
        return pattern is null ? null : new GenericCallView($"Search for {pattern}", ToolCallKind.Search, pattern);
    }

    /// <inheritdoc />
    public override ToolResultView? PresentResult(JsonValue args, ToolResult result)
    {
        if (result.IsError || result.Meta is not JsonObject map) return null;

        var files = new List<SearchFileMatches>();
        if (map.Get("files") is JsonArray array)
        {
            foreach (var entry in array.Items)
            {
                if (entry is not JsonObject file) continue;
                var matches = new List<SearchMatch>();
                if (file.Get("matches") is JsonArray lines)
                {
                    foreach (var line in lines.Items)
                    {
                        if (line is not JsonObject match) continue;
                        matches.Add(new SearchMatch(
                            (int)((match.Get("lineNumber") as JsonNumber)?.Value ?? 0),
                            (match.Get("line") as JsonString)?.Value ?? string.Empty));
                    }
                }

                files.Add(new SearchFileMatches((file.Get("path") as JsonString)?.Value ?? string.Empty, matches));
            }
        }

        return new SearchResultView(
            files,
            null,
            (int)((map.Get("total") as JsonNumber)?.Value ?? 0),
            (map.Get("truncated") as JsonBool)?.Value == true,
            $"Search for {StringArg(args, "pattern")}");
    }

    /// <inheritdoc />
    public override async Task<JsonValue> ExecuteAsync(JsonValue args, ToolRunContext exec)
    {
        var patternText = StringArg(args, "pattern")!;
        var root = StringArg(args, "path") ?? ".";
        var include = StringArg(args, "include");

        Regex pattern;
        try
        {
            pattern = new Regex(patternText, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException error)
        {
            throw new ToolEditException($"pattern is not a valid regular expression: {error.Message}");
        }

        var stat = _fs.Stat(root);
        var candidates = stat is { IsDirectory: false }
            ? [stat]
            : _fs.Glob(root, include ?? "**/*", 5_000, exec.CancellationToken).Files;

        var files = new List<JsonValue>();
        var total = 0;
        var truncated = false;

        foreach (var file in candidates)
        {
            exec.CancellationToken.ThrowIfCancellationRequested();
            if (total >= MatchLimit)
            {
                truncated = true;
                break;
            }

            string text;
            try
            {
                text = await _fs.ReadTextAsync(file.Path, exec.CancellationToken);
            }
            catch (FileSystemException)
            {
                // Unreadable or binary files are simply not part of a text search.
                continue;
            }

            var lines = Dsh.Util.TextDiff.SplitLines(text);
            var matches = new List<JsonValue>();
            for (var index = 0; index < lines.Count && total < MatchLimit; index++)
            {
                if (!pattern.IsMatch(lines[index])) continue;
                matches.Add(JsonValue.From(new Dictionary<string, object?>
                {
                    ["lineNumber"] = index + 1,
                    ["line"] = lines[index].Length > 500 ? lines[index][..500] + "…" : lines[index],
                }));
                total++;
            }

            if (matches.Count > 0)
            {
                files.Add(JsonValue.From(new Dictionary<string, object?>
                {
                    ["path"] = file.Path,
                    ["matches"] = matches,
                }));
            }
        }

        return JsonValue.From(new Dictionary<string, object?>
        {
            ["files"] = files,
            ["total"] = total,
            ["truncated"] = truncated || total >= MatchLimit,
        });
    }

    private static IReadOnlyList<ContentBlock> Render(JsonValue args, JsonValue value)
    {
        var map = (JsonObject)value;
        var files = map.Get("files") as JsonArray;
        if (files is null || files.Items.Count == 0) return [new TextBlock("No matches.")];

        var builder = new StringBuilder();
        foreach (var entry in files.Items)
        {
            if (entry is not JsonObject file) continue;
            builder.AppendLine((file.Get("path") as JsonString)?.Value ?? string.Empty);
            if (file.Get("matches") is not JsonArray matches) continue;
            foreach (var line in matches.Items)
            {
                if (line is not JsonObject match) continue;
                var number = (int)((match.Get("lineNumber") as JsonNumber)?.Value ?? 0);
                builder.Append("  ").Append(number).Append(": ")
                    .AppendLine((match.Get("line") as JsonString)?.Value ?? string.Empty);
            }
        }

        if ((map.Get("truncated") as JsonBool)?.Value == true)
        {
            builder.AppendLine($"[capped at {MatchLimit} matches; narrow the pattern or path for more]");
        }

        return [new TextBlock(builder.ToString().TrimEnd('\n'))];
    }
}

/// <summary>Mounts the file tools.</summary>
public static class FsTools
{
    /// <summary>
    /// Mount read, write, edit, glob, and grep.
    /// </summary>
    /// <returns>The plugin to hand to <see cref="Context.Plugin" />.</returns>
    /// <remarks>
    /// The changing tools grow their escalation properties only when a confining
    /// backend is mounted, so what the model is offered follows the composition rather
    /// than being hardcoded into a schema.
    /// </remarks>
    public static IPlugin Plugin()
        => new FunctionPlugin(
            "tool-fs",
            ctx =>
            {
                var fs = ctx.Require<FileSystemService>(FsKeys.Service);
                var tools = ctx.Require<ToolRuntime>(ToolKeys.Service);
                var confined = ctx.Get<ISandboxPolicy>(SandboxKeys.Service) is { } policy
                               && policy.State.Sandbox != SandboxMode.DangerFullAccess;

                tools.Register(ctx, new ReadTool(fs));
                tools.Register(ctx, new WriteTool(fs, confined));
                tools.Register(ctx, new EditTool(fs, confined));
                tools.Register(ctx, new GlobTool(fs));
                tools.Register(ctx, new GrepTool(fs));
                return Task.CompletedTask;
            },
            FsKeys.Service,
            ToolKeys.Service);
}

using Dsh.Cordis;
using Dsh.Fs;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Tools;
using Dsh.Tools.Fs;

namespace Dsh.Tests.Capabilities;

public sealed class FileToolTests : IDisposable
{
    private readonly string _workspace;
    private readonly Context _ctx;
    private readonly LocalFileSystem _fs;

    public FileToolTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "dsh-fs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspace);
        _ctx = Context.CreateRoot();
        _fs = new LocalFileSystem(_ctx, _workspace);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
    }

    private string Write(string relative, string contents)
    {
        var path = Path.Combine(_workspace, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private static JsonValue Args(params (string Key, object? Value)[] pairs)
        => JsonValue.From(pairs.ToDictionary(static pair => pair.Key, static pair => pair.Value));

    private static ToolRunContext Run() => new(
        new ToolExecution(new ToolExecutionInput(new CallId("c"), "t", JsonValue.Null), Guid.NewGuid()),
        CancellationToken.None);

    [Fact]
    public async Task Read_returns_line_numbered_content()
    {
        Write("a.txt", "alpha\nbeta\ngamma\n");
        var tool = new ReadTool(_fs);

        var value = await tool.ExecuteAsync(Args(("file_path", "a.txt")), Run());
        var content = tool.Output.Render(Args(("file_path", "a.txt")), value);

        var text = ContentBlocks.FlattenText(content);
        Assert.Contains("     1\talpha", text, StringComparison.Ordinal);
        Assert.Contains("     3\tgamma", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_honours_offset_and_limit_and_says_when_more_follows()
    {
        Write("a.txt", string.Join("\n", Enumerable.Range(1, 10).Select(static index => $"line {index}")));
        var args = Args(("file_path", "a.txt"), ("offset", 3), ("limit", 2));

        var value = (JsonObject)await new ReadTool(_fs).ExecuteAsync(args, Run());

        Assert.Equal(2, ((JsonArray)value.Get("lines")!).Items.Count);
        Assert.True(((JsonBool)value.Get("truncated")!).Value);
        Assert.Equal(10d, ((JsonNumber)value.Get("totalLines")!).Value);
    }

    [Fact]
    public async Task Read_refuses_a_directory_with_a_reason_the_model_can_act_on()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "sub"));

        var error = await Assert.ThrowsAsync<FileSystemException>(
            () => new ReadTool(_fs).ExecuteAsync(Args(("file_path", "sub")), Run()));

        Assert.Equal(FileSystemCodes.WrongKind, error.Code);
    }

    [Fact]
    public async Task Read_refuses_a_binary_file_rather_than_handing_the_model_noise()
    {
        File.WriteAllBytes(Path.Combine(_workspace, "blob.bin"), [0x00, 0x01, 0x02]);

        var error = await Assert.ThrowsAsync<FileSystemException>(
            () => new ReadTool(_fs).ExecuteAsync(Args(("file_path", "blob.bin")), Run()));

        Assert.Equal(FileSystemCodes.NotText, error.Code);
    }

    [Fact]
    public async Task Write_creates_a_file_and_reports_the_creation()
    {
        var args = Args(("file_path", "new.txt"), ("content", "hello\n"));

        var value = (JsonObject)await new WriteTool(_fs).ExecuteAsync(args, Run());

        Assert.True(((JsonBool)value.Get("created")!).Value);
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(_workspace, "new.txt")));
    }

    [Fact]
    public async Task Write_over_an_existing_file_keeps_the_prior_text_for_the_diff_card()
    {
        Write("a.txt", "before\n");
        var args = Args(("file_path", "a.txt"), ("content", "after\n"));
        var tool = new WriteTool(_fs);

        var value = await tool.ExecuteAsync(args, Run());
        var view = tool.PresentResult(args, new ToolResult([], false, value));

        var diff = Assert.IsType<DiffResultView>(view);
        var file = Assert.Single(diff.Diffs);
        Assert.Equal("before\n", file.OldText);
        Assert.Equal("after\n", file.NewText);
    }

    [Fact]
    public void The_write_call_card_shows_the_change_without_claiming_to_know_the_prior_text()
    {
        var args = Args(("file_path", "a.txt"), ("content", "new content"));

        var view = Assert.IsType<DiffCallView>(new WriteTool(_fs).PresentCall(args));

        // A call-time presenter has not read the file, so it must not invent a prior side.
        Assert.Null(Assert.Single(view.Diffs).OldText);
        Assert.Equal("new content", view.Diffs[0].NewText);
    }

    [Fact]
    public async Task Edit_replaces_a_unique_match()
    {
        Write("a.txt", "one two three\n");
        var args = Args(("file_path", "a.txt"), ("old_string", "two"), ("new_string", "TWO"));

        await new EditTool(_fs).ExecuteAsync(args, Run());

        Assert.Equal("one TWO three\n", File.ReadAllText(Path.Combine(_workspace, "a.txt")));
    }

    [Fact]
    public async Task Edit_refuses_an_ambiguous_match_and_says_how_many_there_are()
    {
        Write("a.txt", "x\nx\n");
        var args = Args(("file_path", "a.txt"), ("old_string", "x"), ("new_string", "y"));

        var error = await Assert.ThrowsAsync<ToolEditException>(
            () => new EditTool(_fs).ExecuteAsync(args, Run()));

        Assert.Contains("appears 2 times", error.Message, StringComparison.Ordinal);
        Assert.Equal("x\nx\n", File.ReadAllText(Path.Combine(_workspace, "a.txt")));
    }

    [Fact]
    public async Task Edit_replaces_every_match_when_asked_to()
    {
        Write("a.txt", "x\nx\n");
        var args = Args(("file_path", "a.txt"), ("old_string", "x"), ("new_string", "y"), ("replace_all", true));

        var value = (JsonObject)await new EditTool(_fs).ExecuteAsync(args, Run());

        Assert.Equal(2d, ((JsonNumber)value.Get("replacements")!).Value);
        Assert.Equal("y\ny\n", File.ReadAllText(Path.Combine(_workspace, "a.txt")));
    }

    [Fact]
    public async Task Edit_says_plainly_when_the_text_is_not_there()
    {
        Write("a.txt", "content\n");
        var args = Args(("file_path", "a.txt"), ("old_string", "missing"), ("new_string", "x"));

        var error = await Assert.ThrowsAsync<ToolEditException>(
            () => new EditTool(_fs).ExecuteAsync(args, Run()));

        Assert.Contains("was not found", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Glob_finds_files_at_any_depth_and_never_returns_directories()
    {
        Write("src/a.cs", "a");
        Write("src/nested/b.cs", "b");
        Write("src/c.txt", "c");

        var value = (JsonObject)await new GlobTool(_fs).ExecuteAsync(Args(("pattern", "*.cs")), Run());
        var paths = ((JsonArray)value.Get("paths")!).Items.Cast<JsonString>().Select(static entry => entry.Value).ToArray();

        Assert.Equal(2, paths.Length);
        Assert.All(paths, static path => Assert.EndsWith(".cs", path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Grep_reports_matching_lines_with_their_numbers()
    {
        Write("a.txt", "alpha\nbeta\ngamma\n");

        var value = (JsonObject)await new GrepTool(_fs).ExecuteAsync(Args(("pattern", "^b")), Run());
        var files = (JsonArray)value.Get("files")!;
        var matches = (JsonArray)((JsonObject)files.Items[0]).Get("matches")!;
        var first = (JsonObject)matches.Items[0];

        Assert.Equal(2d, ((JsonNumber)first.Get("lineNumber")!).Value);
        Assert.Equal("beta", ((JsonString)first.Get("line")!).Value);
    }

    [Fact]
    public async Task Grep_filters_by_the_include_glob()
    {
        Write("a.cs", "needle");
        Write("b.txt", "needle");

        var value = (JsonObject)await new GrepTool(_fs).ExecuteAsync(
            Args(("pattern", "needle"), ("include", "*.cs")),
            Run());

        var files = (JsonArray)value.Get("files")!;
        Assert.Single(files.Items);
        Assert.EndsWith("a.cs", ((JsonString)((JsonObject)files.Items[0]).Get("path")!).Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Grep_says_when_a_pattern_is_not_a_valid_expression()
    {
        var error = await Assert.ThrowsAsync<ToolEditException>(
            () => new GrepTool(_fs).ExecuteAsync(Args(("pattern", "[unclosed")), Run()));

        Assert.Contains("not a valid regular expression", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_confined_deployment_grows_the_escalation_properties_on_the_changing_tools()
    {
        var plain = new WriteTool(_fs).Parameters.ToWire();
        var confined = new WriteTool(_fs, confined: true).Parameters.ToWire();

        var plainProperties = (IReadOnlyDictionary<string, object?>)plain["properties"]!;
        var confinedProperties = (IReadOnlyDictionary<string, object?>)confined["properties"]!;

        Assert.DoesNotContain("sandbox_permissions", plainProperties.Keys);
        Assert.Contains("sandbox_permissions", confinedProperties.Keys);
        Assert.Contains("justification", confinedProperties.Keys);
    }
}

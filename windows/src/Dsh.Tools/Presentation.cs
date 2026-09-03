using Dsh.Llm;

namespace Dsh.Tools;

/// <summary>
/// What kind of thing a call does, so a UI can pick an icon or treatment without
/// knowing the tool.
/// </summary>
public enum ToolCallKind
{
    /// <summary>Reads something without changing it.</summary>
    Read,

    /// <summary>Modifies a file.</summary>
    Edit,

    /// <summary>Removes something.</summary>
    Delete,

    /// <summary>Moves or renames something.</summary>
    Move,

    /// <summary>Looks for matches.</summary>
    Search,

    /// <summary>Runs a command.</summary>
    Execute,

    /// <summary>Retrieves something remote.</summary>
    Fetch,

    /// <summary>Anything else.</summary>
    Other,
}

/// <summary>
/// A file a call touches, so an editor can follow along as work happens.
/// </summary>
/// <param name="Path">The path the tool operated on, as the model named it.</param>
/// <param name="Line">A one-based line to focus, when the call has one.</param>
public sealed record FileLocation(string Path, int? Line = null);

/// <summary>
/// One file's change, for a UI that renders diffs.
/// </summary>
/// <param name="Path">The file being changed.</param>
/// <param name="OldText">
/// The prior content, or null for a create or an overwrite — a call-time presenter
/// has not read the file, so it cannot claim to know what was there.
/// </param>
/// <param name="NewText">The content after the change.</param>
public sealed record FileDiff(string Path, string? OldText, string NewText);

/// <summary>
/// How a pending call should be shown. Tools declare one of these as a pure
/// function of their arguments, which is what lets a UI render a call it has never
/// heard of, and re-render it identically when replaying a stored session.
/// </summary>
public abstract record ToolCallView;

/// <summary>The default card: a titled row with an optional salient input.</summary>
/// <param name="Title">Short, always-visible label describing this call.</param>
/// <param name="Kind">Category for icon and treatment.</param>
/// <param name="RawInput">The one input worth showing expanded; omitted when there is none.</param>
/// <param name="Content">Extra content blocks to show alongside the title.</param>
/// <param name="Locations">Files this call touches, for editor follow-along.</param>
public sealed record GenericCallView(
    string Title,
    ToolCallKind Kind = ToolCallKind.Other,
    string? RawInput = null,
    IReadOnlyList<ContentBlock>? Content = null,
    IReadOnlyList<FileLocation>? Locations = null) : ToolCallView;

/// <summary>A call that is a command running in a directory.</summary>
/// <param name="Title">The command, shown as the card's header.</param>
/// <param name="Description">One line on what the command does.</param>
/// <param name="Cwd">
/// Where it runs. An absolute path is used as-is; a relative one is resolved against
/// the session workspace by the UI, because a pure presenter cannot see it.
/// </param>
public sealed record TerminalCallView(string Title, string? Description = null, string? Cwd = null) : ToolCallView;

/// <summary>A call that creates or modifies files.</summary>
/// <param name="Title">Card header.</param>
/// <param name="Diffs">One entry per file the call changes.</param>
/// <param name="Locations">Files this call modifies, for editor follow-along.</param>
public sealed record DiffCallView(
    string Title,
    IReadOnlyList<FileDiff> Diffs,
    IReadOnlyList<FileLocation>? Locations = null) : ToolCallView;

/// <summary>How a completed call should be shown.</summary>
public abstract record ToolResultView;

/// <summary>A titled result with content.</summary>
/// <param name="Title">Replacement header, or null to keep the pending one.</param>
/// <param name="Content">The content to show.</param>
public sealed record GenericResultView(string? Title = null, IReadOnlyList<ContentBlock>? Content = null)
    : ToolResultView;

/// <summary>A command's output and how it ended.</summary>
/// <param name="Output">What the command wrote.</param>
/// <param name="ExitCode">Its exit status, when it exited normally.</param>
/// <param name="Signal">The signal that killed it, when one did.</param>
/// <param name="Title">Replacement header, or null to keep the pending one.</param>
public sealed record TerminalResultView(
    string Output,
    int? ExitCode = null,
    string? Signal = null,
    string? Title = null) : ToolResultView;

/// <summary>The change a call actually applied.</summary>
/// <param name="Diffs">One entry per changed file.</param>
/// <param name="Title">Replacement header, or null to keep the pending one.</param>
public sealed record DiffResultView(IReadOnlyList<FileDiff> Diffs, string? Title = null) : ToolResultView;

/// <summary>One matching line inside a searched file.</summary>
/// <param name="LineNumber">The one-based line number.</param>
/// <param name="Line">The line's text.</param>
public sealed record SearchMatch(int LineNumber, string Line);

/// <summary>One file's matches.</summary>
/// <param name="Path">The file that matched.</param>
/// <param name="Matches">Its matching lines.</param>
public sealed record SearchFileMatches(string Path, IReadOnlyList<SearchMatch> Matches);

/// <summary>Search results, either as matching lines or as bare paths.</summary>
/// <param name="Files">Per-file matches, for a content search.</param>
/// <param name="Paths">Bare paths, for a name search.</param>
/// <param name="Total">How many results there were before truncation.</param>
/// <param name="Truncated">Whether the shown results are a prefix of the real set.</param>
/// <param name="Title">Replacement header, or null to keep the pending one.</param>
public sealed record SearchResultView(
    IReadOnlyList<SearchFileMatches>? Files,
    IReadOnlyList<string>? Paths,
    int Total,
    bool Truncated,
    string? Title = null) : ToolResultView;

/// <summary>One numbered line of a read file.</summary>
/// <param name="Number">The one-based line number.</param>
/// <param name="Text">The line's text.</param>
public sealed record ReadLine(int Number, string Text);

/// <summary>A file's contents, line-numbered.</summary>
/// <param name="Path">The file that was read.</param>
/// <param name="Offset">The one-based first line shown.</param>
/// <param name="Lines">The lines themselves.</param>
/// <param name="TotalLines">How many lines the file has.</param>
/// <param name="Language">A hint for syntax highlighting.</param>
public sealed record ReadResultView(
    string Path,
    int Offset,
    IReadOnlyList<ReadLine> Lines,
    int TotalLines,
    string? Language = null) : ToolResultView;

/// <summary>One source a web search returned.</summary>
/// <param name="Url">Where it came from.</param>
/// <param name="Title">Its title.</param>
/// <param name="Snippet">The excerpt that matched.</param>
public sealed record WebSource(string Url, string? Title = null, string? Snippet = null);

/// <summary>A web search's sources.</summary>
/// <param name="Sources">What was found.</param>
/// <param name="Answer">A synthesized answer, when the provider returned one.</param>
/// <param name="Truncated">Whether the shown sources are a prefix of the real set.</param>
public sealed record WebSearchResultView(
    IReadOnlyList<WebSource> Sources,
    string? Answer = null,
    bool Truncated = false) : ToolResultView;

/// <summary>
/// The completed outcome a tool's result presenter is given.
/// </summary>
/// <param name="Content">The model-facing content, or the error text on failure.</param>
/// <param name="IsError">Whether the call failed.</param>
/// <param name="Meta">
/// The tool's own presentation payload, threaded verbatim from the log so a replay
/// reproduces the same card.
/// </param>
public sealed record ToolResult(
    IReadOnlyList<ContentBlock> Content,
    bool IsError,
    Session.JsonValue? Meta = null);

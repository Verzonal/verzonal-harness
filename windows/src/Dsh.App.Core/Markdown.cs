using System.Text;

namespace Dsh.App.Core;

/// <summary>One styled run inside a paragraph.</summary>
/// <param name="Text">The run's characters.</param>
/// <param name="Bold">Whether it is emphasized strongly.</param>
/// <param name="Italic">Whether it is emphasized.</param>
/// <param name="Code">Whether it is inline code.</param>
/// <param name="Link">Where it points, when it is a link.</param>
public sealed record MarkdownSpan(
    string Text,
    bool Bold = false,
    bool Italic = false,
    bool Code = false,
    string? Link = null);

/// <summary>One block of a rendered document.</summary>
public abstract record MarkdownBlock;

/// <summary>A paragraph of styled runs.</summary>
/// <param name="Spans">Its runs, in order.</param>
public sealed record MarkdownParagraph(IReadOnlyList<MarkdownSpan> Spans) : MarkdownBlock;

/// <summary>A heading.</summary>
/// <param name="Level">One through six.</param>
/// <param name="Spans">Its runs, in order.</param>
public sealed record MarkdownHeading(int Level, IReadOnlyList<MarkdownSpan> Spans) : MarkdownBlock;

/// <summary>A fenced code block.</summary>
/// <param name="Language">The language named on the fence, when there was one.</param>
/// <param name="Code">The code, verbatim.</param>
public sealed record MarkdownCode(string? Language, string Code) : MarkdownBlock;

/// <summary>A list.</summary>
/// <param name="Ordered">Whether its items are numbered.</param>
/// <param name="Items">Each item's runs.</param>
public sealed record MarkdownList(bool Ordered, IReadOnlyList<IReadOnlyList<MarkdownSpan>> Items) : MarkdownBlock;

/// <summary>A quoted passage.</summary>
/// <param name="Spans">Its runs, in order.</param>
public sealed record MarkdownQuote(IReadOnlyList<MarkdownSpan> Spans) : MarkdownBlock;

/// <summary>A horizontal rule.</summary>
public sealed record MarkdownRule : MarkdownBlock
{
    /// <summary>The shared instance.</summary>
    public static MarkdownRule Instance { get; } = new();
}

/// <summary>
/// Turns the markdown a model writes into blocks a view can lay out.
/// </summary>
/// <remarks>
/// Deliberately small: the subset models actually produce — headings, fenced code,
/// lists, quotes, rules, and inline emphasis, code, and links. Anything it does not
/// recognize stays visible as literal text, because dropping a line a model wrote is
/// worse than rendering it plainly.
/// </remarks>
public static class MarkdownParser
{
    /// <summary>
    /// Parse a document.
    /// </summary>
    /// <param name="text">The markdown to parse.</param>
    /// <returns>Its blocks, in order.</returns>
    public static IReadOnlyList<MarkdownBlock> Parse(string text)
    {
        var blocks = new List<MarkdownBlock>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new MarkdownParagraph(ParseInline(string.Join(" ", paragraph))));
            paragraph.Clear();
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                var language = trimmed[3..].Trim();
                var code = new StringBuilder();
                index++;

                while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    code.AppendLine(lines[index]);
                    index++;
                }

                blocks.Add(new MarkdownCode(
                    language.Length == 0 ? null : language,
                    code.ToString().TrimEnd('\n')));
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            if (IsRule(trimmed))
            {
                FlushParagraph();
                blocks.Add(MarkdownRule.Instance);
                continue;
            }

            if (HeadingLevel(trimmed) is { } level)
            {
                FlushParagraph();
                blocks.Add(new MarkdownHeading(level, ParseInline(trimmed[(level + 1)..].Trim())));
                continue;
            }

            if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                FlushParagraph();
                blocks.Add(new MarkdownQuote(ParseInline(trimmed[2..])));
                continue;
            }

            if (BulletContent(trimmed) is not null || NumberedContent(trimmed) is not null)
            {
                FlushParagraph();
                var ordered = BulletContent(trimmed) is null;
                var items = new List<IReadOnlyList<MarkdownSpan>>();

                while (index < lines.Length)
                {
                    var item = lines[index].TrimStart();
                    var content = ordered ? NumberedContent(item) : BulletContent(item);
                    if (content is null) break;
                    items.Add(ParseInline(content));
                    index++;
                }

                index--;
                blocks.Add(new MarkdownList(ordered, items));
                continue;
            }

            paragraph.Add(trimmed);
        }

        FlushParagraph();
        return blocks;
    }

    /// <summary>
    /// Parse one line's inline styling.
    /// </summary>
    /// <param name="text">The line.</param>
    /// <returns>Its styled runs, in order.</returns>
    public static IReadOnlyList<MarkdownSpan> ParseInline(string text)
    {
        var spans = new List<MarkdownSpan>();
        var buffer = new StringBuilder();

        void Flush()
        {
            if (buffer.Length == 0) return;
            spans.Add(new MarkdownSpan(buffer.ToString()));
            buffer.Clear();
        }

        for (var index = 0; index < text.Length; index++)
        {
            // Inline code wins over emphasis, so `**not bold**` stays literal.
            if (text[index] == '`')
            {
                var close = text.IndexOf('`', index + 1);
                if (close > index)
                {
                    Flush();
                    spans.Add(new MarkdownSpan(text[(index + 1)..close], Code: true));
                    index = close;
                    continue;
                }
            }

            if (text[index] == '[')
            {
                var closeText = text.IndexOf(']', index + 1);
                if (closeText > index
                    && closeText + 1 < text.Length
                    && text[closeText + 1] == '('
                    && text.IndexOf(')', closeText + 2) is var closeUrl and > 0)
                {
                    Flush();
                    spans.Add(new MarkdownSpan(
                        text[(index + 1)..closeText],
                        Link: text[(closeText + 2)..closeUrl]));
                    index = closeUrl;
                    continue;
                }
            }

            if (text.AsSpan(index).StartsWith("**", StringComparison.Ordinal))
            {
                var close = text.IndexOf("**", index + 2, StringComparison.Ordinal);
                if (close > index)
                {
                    Flush();
                    spans.Add(new MarkdownSpan(text[(index + 2)..close], Bold: true));
                    index = close + 1;
                    continue;
                }
            }

            if (text[index] == '*')
            {
                var close = text.IndexOf('*', index + 1);
                if (close > index + 1)
                {
                    Flush();
                    spans.Add(new MarkdownSpan(text[(index + 1)..close], Italic: true));
                    index = close;
                    continue;
                }
            }

            buffer.Append(text[index]);
        }

        Flush();
        return spans.Count == 0 ? [new MarkdownSpan(string.Empty)] : spans;
    }

    private static int? HeadingLevel(string line)
    {
        var level = 0;
        while (level < line.Length && line[level] == '#') level++;
        return level is >= 1 and <= 6 && level < line.Length && line[level] == ' ' ? level : null;
    }

    private static bool IsRule(string line)
        => line.Length >= 3
           && (line.All(static character => character == '-')
               || line.All(static character => character == '*')
               || line.All(static character => character == '_'));

    private static string? BulletContent(string line)
        => line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)
            ? line[2..]
            : null;

    private static string? NumberedContent(string line)
    {
        var digits = 0;
        while (digits < line.Length && char.IsAsciiDigit(line[digits])) digits++;
        return digits > 0 && digits + 1 < line.Length && line[digits] == '.' && line[digits + 1] == ' '
            ? line[(digits + 2)..]
            : null;
    }
}

using System.Text;

namespace Dsh.Util;

/// <summary>A named terminal colour, resolved to a real one by the UI's theme.</summary>
public enum AnsiColor
{
    /// <summary>No colour was set.</summary>
    Default,

    /// <summary>Black, or the theme's darkest ink.</summary>
    Black,

    /// <summary>Red, conventionally an error.</summary>
    Red,

    /// <summary>Green, conventionally success.</summary>
    Green,

    /// <summary>Yellow, conventionally a warning.</summary>
    Yellow,

    /// <summary>Blue.</summary>
    Blue,

    /// <summary>Magenta.</summary>
    Magenta,

    /// <summary>Cyan.</summary>
    Cyan,

    /// <summary>White, or the theme's lightest ink.</summary>
    White,
}

/// <summary>One run of terminal text sharing the same styling.</summary>
/// <param name="Text">The run's characters.</param>
/// <param name="Foreground">Its text colour.</param>
/// <param name="Background">Its background colour.</param>
/// <param name="Bold">Whether it is emphasized.</param>
/// <param name="Italic">Whether it is italic.</param>
/// <param name="Underline">Whether it is underlined.</param>
public sealed record AnsiSpan(
    string Text,
    AnsiColor Foreground = AnsiColor.Default,
    AnsiColor Background = AnsiColor.Default,
    bool Bold = false,
    bool Italic = false,
    bool Underline = false);

/// <summary>
/// Turns terminal output into styled runs.
/// </summary>
/// <remarks>
/// Command output arrives with escape sequences in it. Rendering those literally
/// would show a person <c>ESC[31m</c> instead of red text, so they are parsed into
/// runs a UI can style and a plain-text consumer can flatten.
/// </remarks>
public static class AnsiText
{
    /// <summary>
    /// Parse terminal output into styled runs.
    /// </summary>
    /// <param name="text">The raw output.</param>
    /// <returns>
    /// The runs, in order. Sequences this parser does not model are dropped rather
    /// than shown, since a stray escape is never what a reader wants to see.
    /// </returns>
    public static IReadOnlyList<AnsiSpan> Parse(string text)
    {
        var spans = new List<AnsiSpan>();
        var buffer = new StringBuilder();
        var foreground = AnsiColor.Default;
        var background = AnsiColor.Default;
        var bold = false;
        var italic = false;
        var underline = false;

        void Flush()
        {
            if (buffer.Length == 0) return;
            spans.Add(new AnsiSpan(buffer.ToString(), foreground, background, bold, italic, underline));
            buffer.Clear();
        }

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '' || index + 1 >= text.Length || text[index + 1] != '[')
            {
                buffer.Append(text[index]);
                continue;
            }

            var end = text.IndexOf('m', index + 2);
            if (end < 0)
            {
                // An unterminated sequence is truncated output, not text to display.
                break;
            }

            Flush();
            foreach (var code in ParseCodes(text[(index + 2)..end]))
            {
                switch (code)
                {
                    case 0:
                        foreground = AnsiColor.Default;
                        background = AnsiColor.Default;
                        bold = italic = underline = false;
                        break;
                    case 1:
                        bold = true;
                        break;
                    case 3:
                        italic = true;
                        break;
                    case 4:
                        underline = true;
                        break;
                    case 22:
                        bold = false;
                        break;
                    case 23:
                        italic = false;
                        break;
                    case 24:
                        underline = false;
                        break;
                    case 39:
                        foreground = AnsiColor.Default;
                        break;
                    case 49:
                        background = AnsiColor.Default;
                        break;
                    case >= 30 and <= 37:
                        foreground = (AnsiColor)(code - 29);
                        break;
                    case >= 90 and <= 97:
                        foreground = (AnsiColor)(code - 89);
                        break;
                    case >= 40 and <= 47:
                        background = (AnsiColor)(code - 39);
                        break;
                    case >= 100 and <= 107:
                        background = (AnsiColor)(code - 99);
                        break;
                    default:
                        break;
                }
            }

            index = end;
        }

        Flush();
        return spans;
    }

    /// <summary>
    /// Remove every escape sequence, leaving the text a person would read.
    /// </summary>
    /// <param name="text">The raw output.</param>
    /// <returns>The text with styling removed.</returns>
    public static string Strip(string text)
    {
        if (!text.Contains('', StringComparison.Ordinal)) return text;
        var builder = new StringBuilder(text.Length);
        foreach (var span in Parse(text)) builder.Append(span.Text);
        return builder.ToString();
    }

    private static IEnumerable<int> ParseCodes(string body)
    {
        if (body.Length == 0)
        {
            yield return 0;
            yield break;
        }

        foreach (var part in body.Split(';'))
        {
            if (int.TryParse(part, System.Globalization.CultureInfo.InvariantCulture, out var code)) yield return code;
        }
    }
}

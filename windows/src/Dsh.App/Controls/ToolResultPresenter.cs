using System.Collections.Generic;
using System.Linq;
using Dsh.Tools;
using Dsh.Util;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

// Dsh.Llm.TextBlock is a model content block and this file builds WinUI TextBlocks,
// so the model vocabulary is reached by name rather than imported wholesale.
using ContentBlocks = Dsh.Llm.ContentBlocks;

namespace Dsh.App.Controls;

/// <summary>
/// Draws a completed tool call as the card its own render intent asks for.
/// </summary>
/// <remarks>
/// The tool decides how its result is shown; this class only knows the closed set of
/// intents. A tool the app has never heard of therefore still draws correctly, and a
/// tool that offers no view — or whose presenter misbehaved and produced nothing —
/// falls back to its raw model-facing text rather than an empty card.
/// </remarks>
public sealed partial class ToolResultPresenter : ContentControl
{
    /// <summary>How many lines of a long output are drawn before it is cut.</summary>
    /// <remarks>
    /// A card is a summary. Anything longer than this is faster to read in the file or
    /// the terminal it came from, and drawing all of it would stall the list.
    /// </remarks>
    private const int MaxLines = 400;

    /// <summary>The tool's own result presentation, when it offered one.</summary>
    public static readonly DependencyProperty ViewProperty = DependencyProperty.Register(
        nameof(View),
        typeof(object),
        typeof(ToolResultPresenter),
        new PropertyMetadata(null, OnChanged));

    /// <summary>The result's flattened text, drawn when no view applies.</summary>
    public static readonly DependencyProperty FallbackTextProperty = DependencyProperty.Register(
        nameof(FallbackText),
        typeof(string),
        typeof(ToolResultPresenter),
        new PropertyMetadata(null, OnChanged));

    /// <summary>Initialize the presenter.</summary>
    public ToolResultPresenter()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        IsTabStop = false;
    }

    /// <summary>The result view to draw.</summary>
    public object? View
    {
        get => GetValue(ViewProperty);
        set => SetValue(ViewProperty, value);
    }

    /// <summary>The text to draw when there is no view.</summary>
    public string? FallbackText
    {
        get => (string?)GetValue(FallbackTextProperty);
        set => SetValue(FallbackTextProperty, value);
    }

    private static void OnChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((ToolResultPresenter)sender).Rebuild();

    private void Rebuild() => Content = View switch
    {
        TerminalResultView terminal => Terminal(terminal),
        DiffResultView diff => Diff(diff.Diffs),
        SearchResultView search => Search(search),
        ReadResultView read => Read(read),
        WebSearchResultView web => Web(web),
        GenericResultView generic => Generic(generic),
        _ => Fallback(),
    };

    private FrameworkElement Fallback() => string.IsNullOrWhiteSpace(FallbackText)
        ? new TextBlock
        {
            Text = "No output.",
            Style = MetaStyle,
            Foreground = MarkdownPresenter.Brush("TextFillColorTertiaryBrush"),
        }
        : Monospace(FallbackText!);

    private FrameworkElement Generic(GenericResultView generic)
    {
        var text = generic.Content is { Count: > 0 } content
            ? ContentBlocks.FlattenText(content)
            : FallbackText;

        return string.IsNullOrWhiteSpace(text)
            ? Fallback()
            : new MarkdownPresenter { PlainText = text };
    }

    private static FrameworkElement Terminal(TerminalResultView terminal)
    {
        var stack = new StackPanel { Spacing = 8 };

        // The status is the first thing a person looks for, so it leads rather than
        // trailing several hundred lines of output.
        stack.Children.Add(ExitPill(terminal));

        if (!string.IsNullOrEmpty(terminal.Output)) stack.Children.Add(Monospace(terminal.Output));

        return stack;
    }

    private static FrameworkElement ExitPill(TerminalResultView terminal)
    {
        var (label, brush) = terminal switch
        {
            { Signal: { Length: > 0 } signal } => ($"killed · {signal}", "SystemFillColorCriticalBrush"),
            { ExitCode: 0 } => ("exit 0", "SystemFillColorSuccessBrush"),
            { ExitCode: { } code } => ($"exit {code}", "SystemFillColorCriticalBrush"),
            _ => ("no exit status", "TextFillColorTertiaryBrush"),
        };

        return new Border
        {
            Padding = new Thickness(8, 2, 8, 2),
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = MarkdownPresenter.Brush("SubtleFillColorSecondaryBrush"),
            Child = new TextBlock
            {
                Text = label,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = MarkdownPresenter.Brush(brush),
            },
        };
    }

    private static FrameworkElement Diff(IReadOnlyList<FileDiff> diffs)
    {
        var stack = new StackPanel { Spacing = 12 };

        foreach (var file in diffs)
        {
            var lines = TextDiff.Compare(file.OldText, file.NewText);
            var added = lines.Count(static line => line.Kind == DiffLineKind.Added);
            var removed = lines.Count(static line => line.Kind == DiffLineKind.Removed);

            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(new TextBlock
            {
                Text = file.Path,
                FontFamily = CodeFont,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            header.Children.Add(new TextBlock
            {
                Text = $"+{added} −{removed}",
                FontSize = 11,
                Foreground = MarkdownPresenter.Brush("TextFillColorTertiaryBrush"),
            });

            stack.Children.Add(header);

            var body = new StackPanel();
            foreach (var hunk in TextDiff.Hunks(lines))
            {
                body.Children.Add(DiffRow(hunk.Header, DiffLineKind.Context, header: true));
                foreach (var line in hunk.Lines) body.Children.Add(DiffRow(Prefixed(line), line.Kind));
            }

            stack.Children.Add(Framed(body));
        }

        return stack;
    }

    private static string Prefixed(DiffLine line) => line.Kind switch
    {
        DiffLineKind.Added => "+" + line.Text,
        DiffLineKind.Removed => "−" + line.Text,
        _ => " " + line.Text,
    };

    private static FrameworkElement DiffRow(string text, DiffLineKind kind, bool header = false)
    {
        var background = kind switch
        {
            DiffLineKind.Added => "SystemFillColorSuccessBackgroundBrush",
            DiffLineKind.Removed => "SystemFillColorCriticalBackgroundBrush",
            _ => null,
        };

        var row = new TextBlock
        {
            Text = text,
            FontFamily = CodeFont,
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true,
        };

        if (header) row.Foreground = MarkdownPresenter.Brush("TextFillColorTertiaryBrush");

        return new Border
        {
            Child = row,
            Padding = new Thickness(8, 1, 8, 1),
            Background = background is null ? null : MarkdownPresenter.Brush(background),
        };
    }

    private static FrameworkElement Search(SearchResultView search)
    {
        var stack = new StackPanel { Spacing = 6 };

        if (search.Files is { Count: > 0 } files)
        {
            foreach (var file in files)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = file.Path,
                    FontFamily = CodeFont,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });

                foreach (var match in file.Matches)
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = $"{match.LineNumber,6}  {match.Line}",
                        FontFamily = CodeFont,
                        FontSize = 12,
                        TextWrapping = TextWrapping.NoWrap,
                        Foreground = MarkdownPresenter.Brush("TextFillColorSecondaryBrush"),
                        IsTextSelectionEnabled = true,
                    });
                }
            }
        }
        else if (search.Paths is { Count: > 0 } paths)
        {
            foreach (var path in paths)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = path,
                    FontFamily = CodeFont,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    IsTextSelectionEnabled = true,
                });
            }
        }
        else
        {
            stack.Children.Add(new TextBlock
            {
                Text = "No matches.",
                Foreground = MarkdownPresenter.Brush("TextFillColorTertiaryBrush"),
            });
        }

        // A truncated search that did not say so would read as a complete one, which is
        // the difference between "nothing else matches" and "there is more to look at".
        if (search.Truncated)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"Showing part of {search.Total} results.",
                FontSize = 11,
                Foreground = MarkdownPresenter.Brush("TextFillColorTertiaryBrush"),
            });
        }

        return Framed(stack);
    }

    private static FrameworkElement Read(ReadResultView read)
    {
        var body = new StackPanel();
        var shown = 0;

        foreach (var line in read.Lines)
        {
            if (shown++ >= MaxLines) break;
            body.Children.Add(new TextBlock
            {
                Text = $"{line.Number,6}  {line.Text}",
                FontFamily = CodeFont,
                FontSize = 12,
                TextWrapping = TextWrapping.NoWrap,
                IsTextSelectionEnabled = true,
            });
        }

        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(new TextBlock
        {
            Text = $"{read.Path} · {read.TotalLines} lines",
            FontSize = 11,
            Foreground = MarkdownPresenter.Brush("TextFillColorTertiaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        stack.Children.Add(Framed(body));

        if (read.Lines.Count > MaxLines)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"Showing the first {MaxLines} of {read.Lines.Count} lines read.",
                FontSize = 11,
                Foreground = MarkdownPresenter.Brush("TextFillColorTertiaryBrush"),
            });
        }

        return stack;
    }

    private static FrameworkElement Web(WebSearchResultView web)
    {
        var stack = new StackPanel { Spacing = 10 };

        if (!string.IsNullOrWhiteSpace(web.Answer))
        {
            stack.Children.Add(new MarkdownPresenter { PlainText = web.Answer });
        }

        foreach (var source in web.Sources)
        {
            var item = new StackPanel { Spacing = 2 };

            var link = new HyperlinkButton
            {
                Content = source.Title ?? source.Url,
                Padding = new Thickness(0),
            };

            if (System.Uri.TryCreate(source.Url, System.UriKind.Absolute, out var uri)) link.NavigateUri = uri;
            item.Children.Add(link);

            if (!string.IsNullOrWhiteSpace(source.Snippet))
            {
                item.Children.Add(new TextBlock
                {
                    Text = source.Snippet,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = MarkdownPresenter.Brush("TextFillColorSecondaryBrush"),
                });
            }

            stack.Children.Add(item);
        }

        if (web.Truncated)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "More sources were found than are shown.",
                FontSize = 11,
                Foreground = MarkdownPresenter.Brush("TextFillColorTertiaryBrush"),
            });
        }

        return stack;
    }

    private static FrameworkElement Monospace(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var shown = lines.Length > MaxLines
            ? string.Join('\n', lines.Take(MaxLines)) + $"\n… {lines.Length - MaxLines} more lines"
            : text;

        return Framed(new TextBlock
        {
            Text = shown,
            FontFamily = CodeFont,
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true,
            Padding = new Thickness(8, 6, 8, 6),
        });
    }

    /// <summary>Put content in a scrollable, bordered box so a wide line does not widen the window.</summary>
    private static FrameworkElement Framed(FrameworkElement content) => new Border
    {
        CornerRadius = new CornerRadius(6),
        Background = MarkdownPresenter.Brush("LayerFillColorDefaultBrush"),
        BorderThickness = new Thickness(1),
        BorderBrush = MarkdownPresenter.Brush("CardStrokeColorDefaultBrush"),
        Child = new ScrollViewer
        {
            Content = content,
            MaxHeight = 420,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        },
    };

    private static FontFamily CodeFont { get; } = new("Cascadia Mono, Consolas, Courier New");

    private static Style? MetaStyle => Application.Current.Resources.TryGetValue("MetaTextStyle", out var value)
        ? value as Style
        : null;
}

using System.Collections.Generic;
using Dsh.App.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace Dsh.App.Controls;

/// <summary>
/// Draws the blocks a message parsed into.
/// </summary>
/// <remarks>
/// Written in code rather than as a template because the block list is a union whose
/// arms need different elements, and a markup selector per arm would be more
/// indirection than the switch it replaces. The parser it draws is unit-tested in
/// <c>Dsh.App.Core</c>; this class only maps each arm to an element.
/// </remarks>
public sealed partial class MarkdownPresenter : ContentControl
{
    /// <summary>The blocks to draw, as produced by <see cref="MarkdownParser" />.</summary>
    /// <remarks>
    /// Typed as <see cref="object" /> so a binding that hands over the wrong thing
    /// leaves the message blank rather than tearing down the conversation view.
    /// </remarks>
    public static readonly DependencyProperty BlocksProperty = DependencyProperty.Register(
        nameof(Blocks),
        typeof(object),
        typeof(MarkdownPresenter),
        new PropertyMetadata(null, OnBlocksChanged));

    /// <summary>Draw text that is not markdown at all, such as a tool's raw output.</summary>
    public static readonly DependencyProperty PlainTextProperty = DependencyProperty.Register(
        nameof(PlainText),
        typeof(string),
        typeof(MarkdownPresenter),
        new PropertyMetadata(null, OnBlocksChanged));

    /// <summary>Initialize the presenter.</summary>
    public MarkdownPresenter()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        IsTabStop = false;
    }

    /// <summary>The blocks to draw.</summary>
    public object? Blocks
    {
        get => GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    /// <summary>Text to draw when no blocks are given.</summary>
    public string? PlainText
    {
        get => (string?)GetValue(PlainTextProperty);
        set => SetValue(PlainTextProperty, value);
    }

    private static void OnBlocksChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((MarkdownPresenter)sender).Rebuild();

    private void Rebuild()
    {
        if (Blocks is not IReadOnlyList<MarkdownBlock> blocks)
        {
            blocks = string.IsNullOrEmpty(PlainText) ? [] : MarkdownParser.Parse(PlainText);
        }

        var stack = new StackPanel { Spacing = 8 };
        foreach (var block in blocks) stack.Children.Add(Element(block));
        Content = stack;
    }

    private static FrameworkElement Element(MarkdownBlock block) => block switch
    {
        MarkdownHeading heading => Heading(heading),
        MarkdownCode code => Code(code),
        MarkdownList list => List(list),
        MarkdownQuote quote => Quote(quote),
        MarkdownRule => new Border
        {
            Height = 1,
            Margin = new Thickness(0, 4, 0, 4),
            Background = Brush("DividerStrokeColorDefaultBrush"),
        },
        MarkdownParagraph paragraph => Paragraph(paragraph.Spans),
        _ => new TextBlock(),
    };

    private static TextBlock Paragraph(IReadOnlyList<MarkdownSpan> spans)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };

        foreach (var span in spans) text.Inlines.Add(Run(span));
        return text;
    }

    private static TextBlock Heading(MarkdownHeading heading)
    {
        var text = Paragraph(heading.Spans);
        text.FontWeight = FontWeights.SemiBold;

        // Levels below three would be smaller than the body text they introduce, which
        // reads as an accident rather than a heading.
        text.FontSize = heading.Level switch
        {
            1 => 22,
            2 => 18,
            _ => 15,
        };

        text.Margin = new Thickness(0, 6, 0, 0);
        return text;
    }

    private static FrameworkElement Code(MarkdownCode code)
    {
        var body = new TextBlock
        {
            Text = code.Code,
            FontFamily = CodeFont,
            FontSize = 13,
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true,
        };

        var scroll = new ScrollViewer
        {
            Content = body,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var stack = new StackPanel { Spacing = 4 };
        if (!string.IsNullOrEmpty(code.Language))
        {
            stack.Children.Add(new TextBlock
            {
                Text = code.Language,
                FontSize = 11,
                Foreground = Brush("TextFillColorTertiaryBrush"),
            });
        }

        stack.Children.Add(scroll);

        return new Border
        {
            Child = stack,
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(6),
            Background = Brush("LayerFillColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush("CardStrokeColorDefaultBrush"),
        };
    }

    private static FrameworkElement List(MarkdownList list)
    {
        var stack = new StackPanel { Spacing = 2 };

        for (var index = 0; index < list.Items.Count; index++)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var marker = new TextBlock
            {
                Text = list.Ordered ? $"{index + 1}." : "•",
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = Brush("TextFillColorSecondaryBrush"),
            };

            var body = Paragraph(list.Items[index]);
            Grid.SetColumn(body, 1);

            row.Children.Add(marker);
            row.Children.Add(body);
            stack.Children.Add(row);
        }

        return stack;
    }

    private static FrameworkElement Quote(MarkdownQuote quote)
    {
        var body = Paragraph(quote.Spans);
        body.Foreground = Brush("TextFillColorSecondaryBrush");

        return new Border
        {
            Child = body,
            Padding = new Thickness(12, 4, 0, 4),
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = Brush("AccentFillColorDefaultBrush"),
        };
    }

    private static Inline Run(MarkdownSpan span)
    {
        if (span.Link is { Length: > 0 } target && System.Uri.TryCreate(target, System.UriKind.Absolute, out var uri))
        {
            var link = new Hyperlink { NavigateUri = uri };
            link.Inlines.Add(new Run { Text = span.Text });
            return link;
        }

        var run = new Run { Text = span.Text };

        if (span.Bold) run.FontWeight = FontWeights.SemiBold;
        if (span.Italic) run.FontStyle = FontStyle.Italic;
        if (span.Code)
        {
            run.FontFamily = CodeFont;
            run.Foreground = Brush("TextFillColorSecondaryBrush");
        }

        return run;
    }

    private static FontFamily CodeFont { get; } = new("Cascadia Mono, Consolas, Courier New");

    /// <summary>
    /// Look up a theme brush by key.
    /// </summary>
    /// <param name="key">The theme resource key.</param>
    /// <returns>The brush, or a transparent one when the key is not in the theme.</returns>
    /// <remarks>
    /// Resolved through the application's resources rather than a template so these
    /// elements follow a light or dark switch like everything built in markup.
    /// </remarks>
    internal static Brush Brush(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
}

using Dsh.App.Core;

namespace Dsh.Tests.App;

public sealed class MarkdownTests
{
    [Fact]
    public void Blank_lines_separate_paragraphs_and_wrapped_lines_join()
    {
        var blocks = MarkdownParser.Parse("one\ntwo\n\nthree");

        Assert.Equal(2, blocks.Count);
        Assert.Equal("one two", Text(blocks[0]));
        Assert.Equal("three", Text(blocks[1]));
    }

    [Fact]
    public void Headings_carry_their_level()
    {
        var blocks = MarkdownParser.Parse("# Title\n\n### Smaller");

        Assert.Equal(1, Assert.IsType<MarkdownHeading>(blocks[0]).Level);
        Assert.Equal(3, Assert.IsType<MarkdownHeading>(blocks[1]).Level);
    }

    [Fact]
    public void A_hash_without_a_space_is_ordinary_text()
    {
        Assert.IsType<MarkdownParagraph>(Assert.Single(MarkdownParser.Parse("#hashtag")));
    }

    [Fact]
    public void Fenced_code_keeps_its_language_and_its_content_verbatim()
    {
        var code = Assert.IsType<MarkdownCode>(
            Assert.Single(MarkdownParser.Parse("```csharp\nvar x = 1;\n// **not bold**\n```")));

        Assert.Equal("csharp", code.Language);
        Assert.Equal("var x = 1;\n// **not bold**", code.Code);
    }

    [Fact]
    public void An_unclosed_fence_still_produces_a_code_block()
    {
        var code = Assert.IsType<MarkdownCode>(Assert.Single(MarkdownParser.Parse("```\nunterminated")));

        Assert.Equal("unterminated", code.Code);
    }

    [Fact]
    public void Bullet_and_numbered_lists_are_told_apart()
    {
        var blocks = MarkdownParser.Parse("- one\n- two\n\n1. first\n2. second");

        var bullets = Assert.IsType<MarkdownList>(blocks[0]);
        var numbered = Assert.IsType<MarkdownList>(blocks[1]);

        Assert.False(bullets.Ordered);
        Assert.Equal(2, bullets.Items.Count);
        Assert.True(numbered.Ordered);
        Assert.Equal(2, numbered.Items.Count);
    }

    [Fact]
    public void Quotes_and_rules_are_recognized()
    {
        var blocks = MarkdownParser.Parse("> quoted\n\n---");

        Assert.IsType<MarkdownQuote>(blocks[0]);
        Assert.IsType<MarkdownRule>(blocks[1]);
    }

    [Fact]
    public void Inline_emphasis_code_and_links_become_styled_runs()
    {
        var spans = MarkdownParser.ParseInline("plain **bold** *italic* `code` [text](https://example.test)");

        Assert.Contains(spans, static span => span.Bold && span.Text == "bold");
        Assert.Contains(spans, static span => span.Italic && span.Text == "italic");
        Assert.Contains(spans, static span => span.Code && span.Text == "code");
        Assert.Contains(spans, static span => span.Link == "https://example.test" && span.Text == "text");
    }

    [Fact]
    public void Inline_code_wins_over_emphasis_inside_it()
    {
        var spans = MarkdownParser.ParseInline("`**not bold**`");

        var span = Assert.Single(spans);
        Assert.True(span.Code);
        Assert.Equal("**not bold**", span.Text);
    }

    [Fact]
    public void Unmatched_markers_stay_visible_rather_than_being_dropped()
    {
        var spans = MarkdownParser.ParseInline("a ** dangling and `unclosed");

        Assert.Equal("a ** dangling and `unclosed", string.Concat(spans.Select(static span => span.Text)));
    }

    [Fact]
    public void An_empty_document_parses_to_nothing()
    {
        Assert.Empty(MarkdownParser.Parse(string.Empty));
    }

    private static string Text(MarkdownBlock block) => block switch
    {
        MarkdownParagraph paragraph => string.Concat(paragraph.Spans.Select(static span => span.Text)),
        MarkdownHeading heading => string.Concat(heading.Spans.Select(static span => span.Text)),
        MarkdownQuote quote => string.Concat(quote.Spans.Select(static span => span.Text)),
        MarkdownCode code => code.Code,
        _ => string.Empty,
    };
}

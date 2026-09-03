using Dsh.Fs;
using Dsh.Util;

namespace Dsh.Tests.Util;

public sealed class TextDiffTests
{
    [Fact]
    public void Identical_text_produces_no_hunks()
    {
        var lines = TextDiff.Compare("one\ntwo\n", "one\ntwo\n");

        Assert.All(lines, static line => Assert.Equal(DiffLineKind.Context, line.Kind));
        Assert.Empty(TextDiff.Hunks(lines));
    }

    [Fact]
    public void A_changed_line_shows_as_a_removal_followed_by_an_addition()
    {
        var lines = TextDiff.Compare("one\ntwo\nthree\n", "one\nTWO\nthree\n");

        Assert.Contains(lines, static line => line.Kind == DiffLineKind.Removed && line.Text == "two");
        Assert.Contains(lines, static line => line.Kind == DiffLineKind.Added && line.Text == "TWO");
    }

    [Fact]
    public void A_new_file_is_all_additions()
    {
        var lines = TextDiff.Compare(null, "first\nsecond\n");

        Assert.Equal(2, lines.Count);
        Assert.All(lines, static line => Assert.Equal(DiffLineKind.Added, line.Kind));
    }

    [Fact]
    public void Line_numbers_track_each_side_independently()
    {
        var lines = TextDiff.Compare("a\nb\n", "a\nx\nb\n");
        var added = lines.Single(static line => line.Kind == DiffLineKind.Added);

        Assert.Equal(2, added.NewNumber);
        Assert.Null(added.OldNumber);
    }

    [Fact]
    public void Distant_changes_become_separate_hunks()
    {
        var before = string.Join("\n", Enumerable.Range(1, 40).Select(static index => $"line {index}"));
        var after = before.Replace("line 2\n", "changed 2\n", StringComparison.Ordinal)
            .Replace("line 38", "changed 38", StringComparison.Ordinal);

        var hunks = TextDiff.Hunks(TextDiff.Compare(before, after));

        Assert.Equal(2, hunks.Count);
    }

    [Fact]
    public void The_rendered_diff_marks_each_line_the_way_a_unified_diff_does()
    {
        var rendered = TextDiff.Render("one\ntwo\n", "one\ntwo\nthree\n");

        Assert.Contains("@@", rendered, StringComparison.Ordinal);
        Assert.Contains("+three", rendered, StringComparison.Ordinal);
        Assert.Contains(" one", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Splitting_lines_does_not_invent_a_trailing_empty_one()
    {
        Assert.Equal(["a", "b"], TextDiff.SplitLines("a\nb\n"));
        Assert.Equal(["a", "b"], TextDiff.SplitLines("a\nb"));
        Assert.Empty(TextDiff.SplitLines(string.Empty));
    }

    [Fact]
    public void Windows_line_endings_compare_the_same_as_unix_ones()
    {
        Assert.Empty(TextDiff.Hunks(TextDiff.Compare("a\r\nb\r\n", "a\nb\n")));
    }
}

public sealed class AnsiTextTests
{
    [Fact]
    public void Plain_text_is_one_unstyled_run()
    {
        var span = Assert.Single(AnsiText.Parse("hello"));

        Assert.Equal("hello", span.Text);
        Assert.Equal(AnsiColor.Default, span.Foreground);
    }

    [Fact]
    public void A_colour_sequence_starts_a_styled_run()
    {
        var spans = AnsiText.Parse("normal [31mred[0m done");

        Assert.Equal(3, spans.Count);
        Assert.Equal(AnsiColor.Default, spans[0].Foreground);
        Assert.Equal(AnsiColor.Red, spans[1].Foreground);
        Assert.Equal("red", spans[1].Text);
        Assert.Equal(AnsiColor.Default, spans[2].Foreground);
    }

    [Fact]
    public void Attributes_combine_and_reset_together()
    {
        var spans = AnsiText.Parse("[1;4;32mloud[0mquiet");

        Assert.True(spans[0].Bold);
        Assert.True(spans[0].Underline);
        Assert.Equal(AnsiColor.Green, spans[0].Foreground);
        Assert.False(spans[1].Bold);
    }

    [Fact]
    public void Stripping_leaves_exactly_what_a_person_would_read()
    {
        Assert.Equal("error: bad", AnsiText.Strip("[31merror[0m: bad"));
        Assert.Equal("plain", AnsiText.Strip("plain"));
    }

    [Fact]
    public void An_unterminated_sequence_is_treated_as_truncated_output()
    {
        Assert.Equal("before ", AnsiText.Strip("before [31"));
    }
}

public sealed class GlobMatcherTests
{
    [Theory]
    [InlineData("*.cs", "Program.cs", true)]
    [InlineData("*.cs", "src/deep/Program.cs", true)]
    [InlineData("*.cs", "Program.ts", false)]
    [InlineData("src/*.cs", "src/Program.cs", true)]
    [InlineData("src/*.cs", "src/deep/Program.cs", false)]
    [InlineData("src/**/*.cs", "src/deep/Program.cs", true)]
    [InlineData("src/**/*.cs", "src/Program.cs", true)]
    [InlineData("*.{js,jsx}", "app.jsx", true)]
    [InlineData("*.{js,jsx}", "app.ts", false)]
    [InlineData("test?.txt", "test1.txt", true)]
    [InlineData("test?.txt", "test10.txt", false)]
    public void Patterns_match_the_way_someone_writing_them_would_expect(
        string pattern,
        string path,
        bool expected)
    {
        Assert.Equal(expected, new GlobMatcher(pattern).Matches(path));
    }

    [Fact]
    public void A_pattern_with_no_separator_searches_the_whole_tree()
    {
        var matcher = new GlobMatcher("README.md");

        Assert.True(matcher.Matches("README.md"));
        Assert.True(matcher.Matches("docs/nested/README.md"));
    }

    [Fact]
    public void A_pattern_with_a_separator_anchors_the_depth()
    {
        var matcher = new GlobMatcher("docs/README.md");

        Assert.True(matcher.Matches("docs/README.md"));
        Assert.False(matcher.Matches("docs/nested/README.md"));
    }

    [Fact]
    public void A_dot_in_a_pattern_is_a_literal_dot()
    {
        Assert.False(new GlobMatcher("a.cs").Matches("axcs"));
    }
}

public sealed class HomePathTests
{
    [Fact]
    public void The_environment_variable_overrides_the_default_location()
    {
        var previous = Environment.GetEnvironmentVariable(HomePaths.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(HomePaths.EnvironmentVariable, "/tmp/dsh-home-test");
            Assert.Equal(Path.GetFullPath("/tmp/dsh-home-test"), HomePaths.Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable(HomePaths.EnvironmentVariable, previous);
        }
    }

    [Fact]
    public void A_blank_environment_value_counts_as_unset()
    {
        var previous = Environment.GetEnvironmentVariable(HomePaths.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(HomePaths.EnvironmentVariable, "   ");
            Assert.EndsWith(HomePaths.DirectoryName, HomePaths.Resolve(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(HomePaths.EnvironmentVariable, previous);
        }
    }

    [Fact]
    public void An_explicit_path_wins_over_the_environment()
    {
        var previous = Environment.GetEnvironmentVariable(HomePaths.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(HomePaths.EnvironmentVariable, "/tmp/from-env");
            Assert.Equal(Path.GetFullPath("/tmp/explicit"), HomePaths.Resolve("/tmp/explicit"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(HomePaths.EnvironmentVariable, previous);
        }
    }
}

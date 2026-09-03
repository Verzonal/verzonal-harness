namespace Dsh.Util;

/// <summary>What one diff line does.</summary>
public enum DiffLineKind
{
    /// <summary>Present in both sides.</summary>
    Context,

    /// <summary>Only in the old side.</summary>
    Removed,

    /// <summary>Only in the new side.</summary>
    Added,
}

/// <summary>One line of a diff.</summary>
/// <param name="Kind">Whether it was kept, removed, or added.</param>
/// <param name="Text">The line's text, without its newline.</param>
/// <param name="OldNumber">Its one-based line number on the old side, when it has one.</param>
/// <param name="NewNumber">Its one-based line number on the new side, when it has one.</param>
public sealed record DiffLine(DiffLineKind Kind, string Text, int? OldNumber, int? NewNumber);

/// <summary>A run of changed lines with the unchanged lines around it.</summary>
/// <param name="OldStart">One-based first old line in the hunk.</param>
/// <param name="OldCount">How many old lines it covers.</param>
/// <param name="NewStart">One-based first new line in the hunk.</param>
/// <param name="NewCount">How many new lines it covers.</param>
/// <param name="Lines">The hunk's lines, in display order.</param>
public sealed record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<DiffLine> Lines)
{
    /// <summary>The <c>@@ -a,b +c,d @@</c> header a unified diff shows.</summary>
    public string Header => $"@@ -{OldStart},{OldCount} +{NewStart},{NewCount} @@";
}

/// <summary>
/// Compares two texts line by line.
/// </summary>
/// <remarks>
/// Every file-writing tool shows what it is about to change, and the same comparison
/// backs both the model-facing text and the diff card a UI draws, so the two can
/// never disagree about what changed.
/// </remarks>
public static class TextDiff
{
    /// <summary>
    /// Compare two texts.
    /// </summary>
    /// <param name="oldText">The prior content; null for a file being created.</param>
    /// <param name="newText">The content after the change.</param>
    /// <returns>Every line, in display order, tagged with what happened to it.</returns>
    public static IReadOnlyList<DiffLine> Compare(string? oldText, string newText)
    {
        var oldLines = SplitLines(oldText ?? string.Empty);
        var newLines = SplitLines(newText);
        if (oldText is null) oldLines = [];

        var lcs = LongestCommonSubsequence(oldLines, newLines);
        var result = new List<DiffLine>();

        int oldIndex = 0, newIndex = 0;
        foreach (var (oldPos, newPos) in lcs)
        {
            while (oldIndex < oldPos)
            {
                result.Add(new DiffLine(DiffLineKind.Removed, oldLines[oldIndex], oldIndex + 1, null));
                oldIndex++;
            }

            while (newIndex < newPos)
            {
                result.Add(new DiffLine(DiffLineKind.Added, newLines[newIndex], null, newIndex + 1));
                newIndex++;
            }

            result.Add(new DiffLine(DiffLineKind.Context, oldLines[oldPos], oldPos + 1, newPos + 1));
            oldIndex = oldPos + 1;
            newIndex = newPos + 1;
        }

        while (oldIndex < oldLines.Count)
        {
            result.Add(new DiffLine(DiffLineKind.Removed, oldLines[oldIndex], oldIndex + 1, null));
            oldIndex++;
        }

        while (newIndex < newLines.Count)
        {
            result.Add(new DiffLine(DiffLineKind.Added, newLines[newIndex], null, newIndex + 1));
            newIndex++;
        }

        return result;
    }

    /// <summary>
    /// Group a comparison into hunks around the changes.
    /// </summary>
    /// <param name="lines">The comparison to group.</param>
    /// <param name="context">How many unchanged lines to keep on each side of a change.</param>
    /// <returns>One hunk per run of changes, or none when nothing changed.</returns>
    public static IReadOnlyList<DiffHunk> Hunks(IReadOnlyList<DiffLine> lines, int context = 3)
    {
        var changed = new List<int>();
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Kind != DiffLineKind.Context) changed.Add(index);
        }

        if (changed.Count == 0) return [];

        var hunks = new List<DiffHunk>();
        var start = Math.Max(0, changed[0] - context);
        var end = Math.Min(lines.Count - 1, changed[0] + context);

        for (var position = 1; position < changed.Count; position++)
        {
            var index = changed[position];
            if (index - context <= end + 1)
            {
                end = Math.Min(lines.Count - 1, index + context);
                continue;
            }

            hunks.Add(BuildHunk(lines, start, end));
            start = Math.Max(0, index - context);
            end = Math.Min(lines.Count - 1, index + context);
        }

        hunks.Add(BuildHunk(lines, start, end));
        return hunks;
    }

    /// <summary>
    /// Render a comparison the way a unified diff reads.
    /// </summary>
    /// <param name="oldText">The prior content; null for a file being created.</param>
    /// <param name="newText">The content after the change.</param>
    /// <param name="context">How many unchanged lines to keep around each change.</param>
    /// <returns>The rendered diff, or an empty string when nothing changed.</returns>
    public static string Render(string? oldText, string newText, int context = 3)
    {
        var hunks = Hunks(Compare(oldText, newText), context);
        if (hunks.Count == 0) return string.Empty;

        var builder = new System.Text.StringBuilder();
        foreach (var hunk in hunks)
        {
            builder.AppendLine(hunk.Header);
            foreach (var line in hunk.Lines)
            {
                var marker = line.Kind switch
                {
                    DiffLineKind.Added => '+',
                    DiffLineKind.Removed => '-',
                    _ => ' ',
                };
                builder.Append(marker).AppendLine(line.Text);
            }
        }

        return builder.ToString().TrimEnd('\n');
    }

    private static DiffHunk BuildHunk(IReadOnlyList<DiffLine> lines, int start, int end)
    {
        var slice = new List<DiffLine>();
        int oldStart = 0, newStart = 0, oldCount = 0, newCount = 0;

        for (var index = start; index <= end; index++)
        {
            var line = lines[index];
            slice.Add(line);
            if (line.OldNumber is { } oldNumber)
            {
                if (oldStart == 0) oldStart = oldNumber;
                oldCount++;
            }

            if (line.NewNumber is { } newNumber)
            {
                if (newStart == 0) newStart = newNumber;
                newCount++;
            }
        }

        return new DiffHunk(
            oldStart == 0 ? 1 : oldStart,
            oldCount,
            newStart == 0 ? 1 : newStart,
            newCount,
            slice);
    }

    private static List<(int Old, int New)> LongestCommonSubsequence(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        var table = new int[left.Count + 1, right.Count + 1];
        for (var oldIndex = left.Count - 1; oldIndex >= 0; oldIndex--)
        {
            for (var newIndex = right.Count - 1; newIndex >= 0; newIndex--)
            {
                table[oldIndex, newIndex] = string.Equals(left[oldIndex], right[newIndex], StringComparison.Ordinal)
                    ? table[oldIndex + 1, newIndex + 1] + 1
                    : Math.Max(table[oldIndex + 1, newIndex], table[oldIndex, newIndex + 1]);
            }
        }

        var matches = new List<(int, int)>();
        int old = 0, current = 0;
        while (old < left.Count && current < right.Count)
        {
            if (string.Equals(left[old], right[current], StringComparison.Ordinal))
            {
                matches.Add((old, current));
                old++;
                current++;
            }
            else if (table[old + 1, current] >= table[old, current + 1])
            {
                old++;
            }
            else
            {
                current++;
            }
        }

        return matches;
    }

    /// <summary>
    /// Split text into lines without inventing a trailing empty one.
    /// </summary>
    /// <param name="text">The text to split.</param>
    /// <returns>The lines, with newline characters removed.</returns>
    public static IReadOnlyList<string> SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0) return lines[..^1];
        return lines;
    }
}

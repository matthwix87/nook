using Nook.Core.Agent;

namespace Nook.Tests;

public class DiffAndEditTests
{
    [Fact]
    public void Diff_marks_added_and_removed_lines()
    {
        var d = Diff.Compute("a\nb\nc\n", "a\nB\nc\nd\n");
        Assert.Equal(" a\n-b\n+B\n c\n+d\n", Diff.ToText(d));
        Assert.Equal((2, 1), Diff.Count(d));
    }

    [Fact]
    public void Diff_of_new_file_is_all_added()
    {
        var d = Diff.Compute("", "x\ny");
        Assert.All(d, l => Assert.Equal(DiffKind.Added, l.Kind));
        Assert.Equal(2, d.Count);
    }

    [Fact]
    public void Diff_context_collapses_unchanged_runs()
    {
        var before = string.Join("\n", Enumerable.Range(1, 30));
        var after = before.Replace("\n15\n", "\nfifteen\n");
        var ctx = Diff.WithContext(Diff.Compute(before, after), 2);
        Assert.Equal(DiffKind.Gap, ctx[0].Kind);
        Assert.Contains(ctx, l => l.Kind == DiffKind.Removed && l.Text == "15");
        Assert.Contains(ctx, l => l.Kind == DiffKind.Added && l.Text == "fifteen");
        Assert.Equal(DiffKind.Gap, ctx[^1].Kind);
        Assert.Equal(1 + 2 + 2 + 2 + 1, ctx.Count);
    }

    [Fact]
    public void Edit_replaces_single_occurrence()
    {
        Assert.Equal("one TWO three", ToolBox.ApplyEdit("one two three", "two", "TWO"));
    }

    [Fact]
    public void Edit_rejects_missing_text()
    {
        var e = Assert.Throws<ToolException>(() => ToolBox.ApplyEdit("abc", "zzz", "y"));
        Assert.Contains("not found", e.Message);
    }

    [Fact]
    public void Edit_rejects_ambiguous_text()
    {
        var e = Assert.Throws<ToolException>(() => ToolBox.ApplyEdit("x = 1\nx = 1\n", "x = 1", "x = 2"));
        Assert.Contains("more than once", e.Message);
    }

    [Fact]
    public void Edit_rejects_empty_old_text()
    {
        Assert.Throws<ToolException>(() => ToolBox.ApplyEdit("abc", "", "y"));
    }

    [Fact]
    public void Edit_handles_crlf_files()
    {
        var result = ToolBox.ApplyEdit("a\r\nb\r\nc\r\n", "a\nb", "a\nB");
        Assert.Equal("a\r\nB\r\nc\r\n", result);
    }
}

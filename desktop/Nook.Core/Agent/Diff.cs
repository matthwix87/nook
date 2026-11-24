using System.Text;

namespace Nook.Core.Agent;

public enum DiffKind { Same, Added, Removed, Gap }

public sealed record DiffLine(DiffKind Kind, string Text, int? OldNo = null, int? NewNo = null)
{
    public string Prefix => Kind switch { DiffKind.Added => "+", DiffKind.Removed => "-", DiffKind.Gap => "", _ => " " };
}

/// <summary>Line diff for the approval view (LCS on the changed middle; good enough for source files).</summary>
public static class Diff
{
    public static string[] Lines(string text) =>
        text.Length == 0 ? Array.Empty<string>() : text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    public static List<DiffLine> Compute(string before, string after)
    {
        var a = Lines(before);
        var b = Lines(after);
        int pre = 0;
        while (pre < a.Length && pre < b.Length && a[pre] == b[pre]) pre++;
        int suf = 0;
        while (suf < a.Length - pre && suf < b.Length - pre && a[^(suf + 1)] == b[^(suf + 1)]) suf++;

        var outp = new List<DiffLine>();
        for (int i = 0; i < pre; i++) outp.Add(new(DiffKind.Same, a[i], i + 1, i + 1));

        int n = a.Length - pre - suf, m = b.Length - pre - suf;
        if ((long)n * m > 4_000_000)
        {
            // Too big for LCS: show the whole middle as replaced.
            for (int i = 0; i < n; i++) outp.Add(new(DiffKind.Removed, a[pre + i], pre + i + 1));
            for (int j = 0; j < m; j++) outp.Add(new(DiffKind.Added, b[pre + j], null, pre + j + 1));
        }
        else
        {
            var lcs = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    lcs[i, j] = a[pre + i] == b[pre + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            int x = 0, y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && a[pre + x] == b[pre + y])
                {
                    outp.Add(new(DiffKind.Same, a[pre + x], pre + x + 1, pre + y + 1));
                    x++; y++;
                }
                else if (x < n && (y == m || lcs[x + 1, y] >= lcs[x, y + 1]))
                {
                    outp.Add(new(DiffKind.Removed, a[pre + x], pre + x + 1));
                    x++;
                }
                else
                {
                    outp.Add(new(DiffKind.Added, b[pre + y], null, pre + y + 1));
                    y++;
                }
            }
        }
        for (int k = suf; k > 0; k--)
            outp.Add(new(DiffKind.Same, a[^k], a.Length - k + 1, b.Length - k + 1));
        return outp;
    }

    /// <summary>Keep only changed lines plus <paramref name="context"/> lines around them; gaps become one Gap line.</summary>
    public static List<DiffLine> WithContext(List<DiffLine> lines, int context = 3)
    {
        var keep = new bool[lines.Count];
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].Kind != DiffKind.Same)
                for (int j = Math.Max(0, i - context); j <= Math.Min(lines.Count - 1, i + context); j++) keep[j] = true;
        var outp = new List<DiffLine>();
        int skipped = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (keep[i])
            {
                if (skipped > 0) outp.Add(new(DiffKind.Gap, $"... {skipped} unchanged line{(skipped == 1 ? "" : "s")} ..."));
                skipped = 0;
                outp.Add(lines[i]);
            }
            else skipped++;
        }
        if (skipped > 0 && outp.Count > 0) outp.Add(new(DiffKind.Gap, $"... {skipped} unchanged line{(skipped == 1 ? "" : "s")} ..."));
        return outp;
    }

    public static string ToText(IEnumerable<DiffLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var l in lines) sb.Append(l.Prefix).Append(l.Text).Append('\n');
        return sb.ToString();
    }

    public static (int Added, int Removed) Count(IEnumerable<DiffLine> lines) =>
        (lines.Count(l => l.Kind == DiffKind.Added), lines.Count(l => l.Kind == DiffKind.Removed));
}

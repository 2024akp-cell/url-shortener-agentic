using System.Text;

namespace Agentic.Orchestration.Workspace;

/// <summary>Minimal LCS-based unified diff, sufficient for reviewable change summaries.</summary>
public static class DiffBuilder
{
    private const int Context = 3;

    public static string Unified(string path, string? original, string? current)
    {
        var a = Split(original);
        var b = Split(current);
        var ops = Diff(a, b);

        var sb = new StringBuilder();
        sb.Append("--- ").Append(original is null ? "/dev/null" : "a/" + path).Append('\n');
        sb.Append("+++ ").Append(current is null ? "/dev/null" : "b/" + path).Append('\n');

        // Group changed lines into hunks, merging hunks whose context windows overlap.
        var hunks = new List<(int Start, int End)>();
        for (var k = 0; k < ops.Count; k++)
        {
            if (ops[k].Kind == ' ')
                continue;
            var hunkStart = Math.Max(0, k - Context);
            var hunkEnd = Math.Min(ops.Count, k + Context + 1);
            if (hunks.Count > 0 && hunkStart <= hunks[^1].End)
                hunks[^1] = (hunks[^1].Start, Math.Max(hunks[^1].End, hunkEnd));
            else
                hunks.Add((hunkStart, hunkEnd));
        }

        foreach (var (start, end) in hunks)
        {
            var aStart = ops[start].A + 1;
            var bStart = ops[start].B + 1;
            var aLen = ops.Skip(start).Take(end - start).Count(o => o.Kind != '+');
            var bLen = ops.Skip(start).Take(end - start).Count(o => o.Kind != '-');
            sb.Append($"@@ -{aStart},{aLen} +{bStart},{bLen} @@\n");
            for (var k = start; k < end; k++)
                sb.Append(ops[k].Kind).Append(ops[k].Text).Append('\n');
        }
        return sb.ToString();
    }

    public static (int Added, int Removed) Stats(string? original, string? current)
    {
        var ops = Diff(Split(original), Split(current));
        return (ops.Count(o => o.Kind == '+'), ops.Count(o => o.Kind == '-'));
    }

    private static string[] Split(string? text) =>
        string.IsNullOrEmpty(text) ? Array.Empty<string>() : text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    private readonly record struct Op(char Kind, string Text, int A, int B);

    private static List<Op> Diff(string[] a, string[] b)
    {
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var x = a.Length - 1; x >= 0; x--)
            for (var y = b.Length - 1; y >= 0; y--)
                lcs[x, y] = a[x] == b[y] ? lcs[x + 1, y + 1] + 1 : Math.Max(lcs[x + 1, y], lcs[x, y + 1]);

        var ops = new List<Op>();
        int i = 0, j = 0;
        while (i < a.Length || j < b.Length)
        {
            if (i < a.Length && j < b.Length && a[i] == b[j])
                ops.Add(new Op(' ', a[i], i++, j++));
            else if (j < b.Length && (i == a.Length || lcs[i, j + 1] >= lcs[i + 1, j]))
                ops.Add(new Op('+', b[j], i, j++));
            else
                ops.Add(new Op('-', a[i], i++, j));
        }
        return ops;
    }
}

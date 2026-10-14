using System.Text;

namespace Seeing.Agent.Tools.Support;

/// <summary>统一行 diff（LCS 行级 + hunk header）。不依赖任何能力包。</summary>
public static class LineDiff
{
    /// <summary>生成 old→new 的统一 diff；无差异返回空串。</summary>
    public static string ComputeUnified(string oldText, string newText, int context = 3)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);
        var ops = BuildOps(oldLines, newLines);
        if (ops.All(o => o.Kind == OpKind.Equal))
            return string.Empty;

        var sb = new StringBuilder();
        sb.Append("--- a\n+++ b\n");

        var changed = ops.Select((o, i) => (o, i)).Where(t => t.o.Kind != OpKind.Equal).Select(t => t.i).ToList();
        var i2 = 0;
        while (i2 < changed.Count)
        {
            var start = Math.Max(0, changed[i2] - context);
            var end = changed[i2];
            while (i2 + 1 < changed.Count && changed[i2 + 1] <= end + context * 2 + 1)
            {
                i2++;
                end = changed[i2];
            }
            end = Math.Min(ops.Count - 1, end + context);
            AppendHunk(sb, ops, start, end);
            i2++;
        }
        return sb.ToString();
    }

    private enum OpKind { Equal, Delete, Insert }
    private readonly record struct Op(OpKind Kind, string Line);

    private static List<string> SplitLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        if (normalized.EndsWith('\n'))
            normalized = normalized[..^1];
        return normalized.Length == 0 ? new List<string>() : normalized.Split('\n').ToList();
    }

    private static List<Op> BuildOps(List<string> a, List<string> b)
    {
        var n = a.Count; var m = b.Count;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var ops = new List<Op>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y]) { ops.Add(new Op(OpKind.Equal, a[x])); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) { ops.Add(new Op(OpKind.Delete, a[x])); x++; }
            else { ops.Add(new Op(OpKind.Insert, b[y])); y++; }
        }
        while (x < n) { ops.Add(new Op(OpKind.Delete, a[x++])); }
        while (y < m) { ops.Add(new Op(OpKind.Insert, b[y++])); }
        return ops;
    }

    private static void AppendHunk(StringBuilder sb, List<Op> ops, int start, int end)
    {
        var oldStart = 1; var newStart = 1;
        for (var k = 0; k < start; k++)
        {
            if (ops[k].Kind != OpKind.Insert) oldStart++;
            if (ops[k].Kind != OpKind.Delete) newStart++;
        }
        var oldCount = 0; var newCount = 0;
        for (var k = start; k <= end; k++)
        {
            if (ops[k].Kind != OpKind.Insert) oldCount++;
            if (ops[k].Kind != OpKind.Delete) newCount++;
        }
        sb.Append($"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@\n");
        for (var k = start; k <= end; k++)
        {
            var prefix = ops[k].Kind switch { OpKind.Equal => " ", OpKind.Delete => "-", _ => "+" };
            sb.Append(prefix).Append(ops[k].Line).Append('\n');
        }
    }
}

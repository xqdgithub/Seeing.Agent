using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Hosting.Execution.ToolExposure;

/// <summary>
/// 工具元信息（目录构建器输入，纯数据）。
/// </summary>
public sealed record ToolMeta(
    string Id,
    string? Namespace,
    string Description,
    int EstimatedTokens);

/// <summary>
/// 工具目录构建器：纯静态，负责行截断、确定性排序、命名空间预算分组。
/// 渲染成 prompt 文本不在此处（由 <see cref="ToolCatalogRenderer"/>）。
/// </summary>
public static class ToolCatalogBuilder
{
    /// <summary>
    /// 构建全量目录条目：生成单行签名、描述截断到 lineMaxChars、按 (namespace, id) 确定性排序。
    /// Mode 先置 Deferred，实际暴露模式由策略覆盖。
    /// </summary>
    public static IReadOnlyList<ToolCatalogEntry> Build(IEnumerable<ToolMeta> metas, int lineMaxChars)
    {
        ArgumentNullException.ThrowIfNull(metas);

        return metas
            .OrderBy(e => e.Namespace ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .Select(meta => ToEntry(meta, lineMaxChars))
            .ToList();
    }

    /// <summary>
    /// 对 deferred 条目按命名空间分组并施加字符预算，产出显示分组：
    /// <para>
    /// 每个命名空间**恒出现在结果中**（含 0 展示），并带 TotalCount；预算不足时该组 Shown 为空。
    /// 分配确定性：① 每命名空间先按最短签名保底 1 行；② 余量按签名行长度升序轮转补全。
    /// </para>
    /// 预算仅约束"展示哪些条目"，不影响可发现性（检索语料为全量 deferred，另由结算出口提供）。
    /// </summary>
    public static IReadOnlyList<ToolCatalogNamespace> BuildNamespaces(
        IReadOnlyList<ToolCatalogEntry> deferred,
        int catalogBudgetChars)
    {
        ArgumentNullException.ThrowIfNull(deferred);

        var groups = deferred
            .GroupBy(e => e.Namespace ?? string.Empty, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.OrderBy(e => e.Id, StringComparer.Ordinal).ToList())
            .ToList();

        var selected = groups.Select(_ => new List<ToolCatalogEntry>()).ToList();
        var remaining = Math.Max(0, catalogBudgetChars);

        // 阶段 1：每命名空间保底 1 行（最短签名优先），放不下则该组本轮 0 展示（仍保留分组）。
        var pending = new List<(int Group, int Index)>();
        for (var i = 0; i < groups.Count; i++)
        {
            var ordered = groups[i].OrderBy(e => e.Signature.Length).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
            if (ordered.Count > 0 && remaining >= ordered[0].Signature.Length)
            {
                selected[i].Add(ordered[0]);
                remaining -= ordered[0].Signature.Length;
                for (var j = 1; j < ordered.Count; j++)
                    pending.Add((i, groups[i].IndexOf(ordered[j])));
            }
            else
            {
                for (var j = 0; j < ordered.Count; j++)
                    pending.Add((i, groups[i].IndexOf(ordered[j])));
            }
        }

        // 阶段 2：余量按签名行长度升序轮转补全（等长按 ns、id 序，确定性）。
        var queue = pending
            .Select(p => (p.Group, Entry: groups[p.Group][p.Index]))
            .Where(p => !selected[p.Group].Contains(p.Entry))
            .OrderBy(x => x.Entry.Signature.Length)
            .ThenBy(x => groups[x.Group][0].Namespace ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(x => x.Entry.Id, StringComparer.Ordinal)
            .ToList();

        foreach (var (group, entry) in queue)
        {
            if (remaining < entry.Signature.Length)
                break;
            selected[group].Add(entry);
            remaining -= entry.Signature.Length;
        }

        // 各组展示条目恢复 id 序，保证渲染确定性。
        var result = new List<ToolCatalogNamespace>(groups.Count);
        for (var i = 0; i < groups.Count; i++)
        {
            var shown = selected[i]
                .OrderBy(e => e.Id, StringComparer.Ordinal)
                .ToList();
            result.Add(new ToolCatalogNamespace(groups[i][0].Namespace ?? string.Empty, groups[i].Count, shown));
        }

        return result;
    }

    private static ToolCatalogEntry ToEntry(ToolMeta meta, int lineMaxChars)
    {
        var summary = Truncate(meta.Description ?? string.Empty, lineMaxChars);
        return new ToolCatalogEntry(
            meta.Id,
            meta.Namespace,
            $"{meta.Id}({summary})",
            summary,
            meta.EstimatedTokens,
            false,
            ToolExposureMode.Deferred);
    }

    // 截断保证总长（含省略号）不超过 maxChars：预留 1 个字符给省略号。
    private static string Truncate(string text, int maxChars)
    {
        if (maxChars <= 0)
            return string.Empty;
        if (text.Length <= maxChars)
            return text;
        if (maxChars == 1)
            return "…";
        return text[..(maxChars - 1)] + "…";
    }
}

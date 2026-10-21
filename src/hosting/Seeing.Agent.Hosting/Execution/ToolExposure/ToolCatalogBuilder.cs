using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Hosting.Execution.ToolExposure;

/// <summary>
/// 工具暴露子系统共享常量。
/// </summary>
internal static class ToolExposureConstants
{
    /// <summary>检索元工具 ID（Tiered 常驻内联；All 模式隐藏）。</summary>
    internal const string SearchToolId = "tool_search";
}

/// <summary>
/// 工具元信息三元组：目录构建器的输入（纯数据）。
/// </summary>
/// <param name="Id">工具 ID。</param>
/// <param name="Namespace">命名空间（可空）。</param>
/// <param name="Signature">可调用签名摘要。</param>
/// <param name="Description">工具描述。</param>
/// <param name="EstimatedTokens">估算 token 数。</param>
/// <param name="Pinned">是否固定（不参与预算裁剪）。</param>
public sealed record ToolMeta(
    string Id,
    string? Namespace,
    string Signature,
    string Description,
    int EstimatedTokens,
    bool Pinned);

/// <summary>
/// 工具目录构建器：纯静态，负责截断、分组、确定性排序与预算裁剪算法（渲染成 prompt 文本不在此处）。
/// </summary>
public static class ToolCatalogBuilder
{
    /// <summary>
    /// 按命名空间分组构建目录条目：生成单行签名、截断摘要、按 (namespace, id) 确定性排序。
    /// Mode 一律先给 Deferred，实际暴露模式由策略覆盖。
    /// </summary>
    /// <param name="metas">工具元信息集合。</param>
    /// <param name="lineMaxChars">单行摘要字符上限。</param>
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
    /// 预算内构建目录：先为每个命名空间保底 1 行，再按签名行长度升序轮转补全，
    /// 直到预算耗尽；未完整展示的命名空间在其首行末尾标注 "(N tools, M shown)"。确定性。
    /// </summary>
    /// <param name="all">全量工具元信息。</param>
    /// <param name="catalogBudgetChars">目录整体字符预算。</param>
    /// <param name="lineMaxChars">单行摘要字符上限。</param>
    public static IReadOnlyList<ToolCatalogEntry> BuildWithinBudget(
        IReadOnlyList<ToolMeta> all,
        int catalogBudgetChars,
        int lineMaxChars)
    {
        ArgumentNullException.ThrowIfNull(all);

        var ordered = Build(all, lineMaxChars);
        var groups = ordered
            .GroupBy(e => e.Namespace ?? string.Empty, StringComparer.Ordinal)
            .Select(g => g.ToList())
            .ToList();

        var descById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var meta in all)
        {
            descById[meta.Id] = meta.Description ?? string.Empty;
        }

        var selected = groups.Select(_ => new List<ToolCatalogEntry>()).ToList();
        var queue = new List<(ToolCatalogEntry Entry, int Group)>();
        var remaining = catalogBudgetChars;
        var budgetExhausted = false;

        // 阶段 1：每命名空间保底 1 行
        for (var i = 0; i < groups.Count && !budgetExhausted; i++)
        {
            var first = groups[i][0];
            if (remaining >= first.Signature.Length)
            {
                selected[i].Add(first);
                remaining -= first.Signature.Length;
            }
            else
            {
                budgetExhausted = true;
            }

            for (var j = 1; j < groups[i].Count; j++)
            {
                queue.Add((groups[i][j], i));
            }
        }

        // 阶段 2：按签名行长度升序轮转补全（长度相等按 ns、id 次序，确定性）
        if (!budgetExhausted)
        {
            var pending = queue
                .OrderBy(x => x.Entry.Signature.Length)
                .ThenBy(x => groups[x.Group][0].Namespace ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(x => x.Entry.Id, StringComparer.Ordinal)
                .ToList();

            foreach (var (entry, group) in pending)
            {
                if (remaining < entry.Signature.Length)
                {
                    break;
                }

                selected[group].Add(entry);
                remaining -= entry.Signature.Length;
            }
        }

        // 阶段 3：未完整展示的命名空间在其首行末尾追加计数标注
        var result = new List<ToolCatalogEntry>();
        for (var i = 0; i < groups.Count; i++)
        {
            var total = groups[i].Count;
            var shown = selected[i].Count;
            if (shown == 0)
            {
                continue;
            }

            if (shown < total)
            {
                var note = $" ({total} tools, {shown} shown)";
                var first = selected[i][0];
                var summary = Truncate(
                    descById.TryGetValue(first.Id, out var desc) ? desc : first.Summary,
                    Math.Max(0, lineMaxChars - note.Length)) + note;
                selected[i][0] = first with
                {
                    Summary = summary,
                    Signature = $"{first.Id}({summary})"
                };
            }

            result.AddRange(selected[i]);
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
            meta.Pinned,
            ToolExposureMode.Deferred);
    }

    private static string Truncate(string text, int maxChars)
    {
        if (maxChars <= 0)
        {
            return string.Empty;
        }

        return text.Length <= maxChars ? text : text[..maxChars] + "…";
    }
}

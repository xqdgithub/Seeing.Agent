using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Hosting.Execution.ToolExposure;

/// <summary>
/// 分层暴露策略：pinned / 已发现 / 检索元工具内联，其余延迟。
/// <para>
/// 纯分层语义——不感知"升级"：<c>auto</c> 是否落到本策略由 resolver 依阈值与粘滞决定
/// （升级 = 从全内联进入分层，而非相反）。因此本策略对 <see cref="ToolExposureInput.Escalated"/> 不敏感。
/// </para>
/// <para>
/// 职责边界：<see cref="ToolExposurePlan.CatalogEntries"/> 输出**全量条目并标注各自 Mode**
/// （与 <see cref="AllToolExposurePolicy"/> 一致），InlineSchemas 留空，由 resolver 依 Mode 物化并排序。
/// </para>
/// </summary>
public sealed class TieredToolExposurePolicy : IToolExposurePolicy
{
    /// <inheritdoc />
    public ToolExposurePolicyMode Mode => ToolExposurePolicyMode.Tiered;

    /// <inheritdoc />
    public ToolExposurePlan Plan(ToolExposureInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var entries = input.Available
            .Select(e => e with { Mode = ResolveMode(e, input) })
            .ToList();

        var inlineCount = entries.Count(e => e.Mode == ToolExposureMode.Inline);
        var deferredCount = entries.Count(e => e.Mode == ToolExposureMode.Deferred);
        // 内联口径：仅 Inline 条目计入定义 token（Deferred 走目录，另计 CatalogTokens）。
        var estimatedTokens = entries
            .Where(e => e.Mode == ToolExposureMode.Inline)
            .Sum(e => e.EstimatedTokens);

        return new ToolExposurePlan(
            Array.Empty<FunctionToolSchema>(),
            entries,
            inlineCount,
            deferredCount,
            estimatedTokens);
    }

    private static ToolExposureMode ResolveMode(ToolCatalogEntry entry, ToolExposureInput input)
    {
        if (string.Equals(entry.Id, ToolIdConventions.SearchToolId, StringComparison.OrdinalIgnoreCase))
        {
            return ToolExposureMode.Inline;
        }

        if (input.DiscoveredIds.Contains(entry.Id))
        {
            return ToolExposureMode.Inline;
        }

        foreach (var pinnedId in input.PinnedIds)
        {
            if (string.Equals(pinnedId, entry.Id, StringComparison.OrdinalIgnoreCase))
            {
                return ToolExposureMode.Inline;
            }
        }

        return ToolExposureMode.Deferred;
    }
}

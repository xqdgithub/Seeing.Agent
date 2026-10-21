using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Hosting.Execution.ToolExposure;

/// <summary>
/// 分层暴露策略：pinned / 已发现 / 检索元工具内联，其余延迟；升级态下全部内联。
/// 职责边界：仅产出各条目的暴露模式与计数，InlineSchemas 留空由调用方按 id 填充。
/// </summary>
public sealed class TieredToolExposurePolicy : IToolExposurePolicy
{
    /// <inheritdoc />
    public ToolExposurePolicyMode Mode => ToolExposurePolicyMode.Tiered;

    /// <inheritdoc />
    public ToolExposurePlan Plan(ToolExposureInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var entries = input.Escalated
            ? input.Available.Select(e => e with { Mode = ToolExposureMode.Inline }).ToList()
            : input.Available.Select(e => e with { Mode = ResolveMode(e, input) }).ToList();

        var inlineCount = entries.Count(e => e.Mode == ToolExposureMode.Inline);
        var deferredCount = entries.Count(e => e.Mode == ToolExposureMode.Deferred);
        var estimatedTokens = entries
            .Where(e => e.Mode != ToolExposureMode.Hidden)
            .Sum(e => e.EstimatedTokens);

        var catalogEntries = entries
            .Where(e => e.Mode == ToolExposureMode.Deferred)
            .ToList();

        return new ToolExposurePlan(
            Array.Empty<FunctionToolSchema>(),
            catalogEntries,
            inlineCount,
            deferredCount,
            estimatedTokens);
    }

    private static ToolExposureMode ResolveMode(ToolCatalogEntry entry, ToolExposureInput input)
    {
        if (entry.Id == ToolExposureConstants.SearchToolId)
        {
            return ToolExposureMode.Inline;
        }

        if (input.DiscoveredIds.Contains(entry.Id))
        {
            return ToolExposureMode.Inline;
        }

        foreach (var pinnedId in input.PinnedIds)
        {
            if (string.Equals(pinnedId, entry.Id, StringComparison.Ordinal))
            {
                return ToolExposureMode.Inline;
            }
        }

        return ToolExposureMode.Deferred;
    }
}

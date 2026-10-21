using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Hosting.Execution.ToolExposure;

/// <summary>
/// 全量内联策略：所有可用工具标记 Inline，检索元工具（tool_search）隐藏（全内联下检索无意义）。
/// 职责边界：仅产出各条目的暴露模式与计数，InlineSchemas 留空由调用方按 id 填充。
/// </summary>
public sealed class AllToolExposurePolicy : IToolExposurePolicy
{
    /// <inheritdoc />
    public ToolExposurePolicyMode Mode => ToolExposurePolicyMode.All;

    /// <inheritdoc />
    public ToolExposurePlan Plan(ToolExposureInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var entries = input.Available
            .Select(e => e with
            {
                Mode = e.Id == ToolExposureConstants.SearchToolId
                    ? ToolExposureMode.Hidden
                    : ToolExposureMode.Inline
            })
            .ToList();

        var inlineCount = entries.Count(e => e.Mode == ToolExposureMode.Inline);
        var deferredCount = entries.Count(e => e.Mode == ToolExposureMode.Deferred);
        var estimatedTokens = entries
            .Where(e => e.Mode != ToolExposureMode.Hidden)
            .Sum(e => e.EstimatedTokens);

        return new ToolExposurePlan(
            Array.Empty<FunctionToolSchema>(),
            entries,
            inlineCount,
            deferredCount,
            estimatedTokens);
    }
}

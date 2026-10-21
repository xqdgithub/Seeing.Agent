namespace Seeing.Agent.Abstractions.Tools;

/// <summary>
/// 工具暴露策略输入：可供策略消费的会话侧事实快照（纯数据）。
/// 仅承载「模式分配」所需字段；目录预算裁剪与升级判定属结算出口职责，不在此（避免装饰性输入）。
/// </summary>
/// <param name="Mode">当前生效的策略模式。</param>
/// <param name="Available">本会话可用工具目录条目全集。</param>
/// <param name="DiscoveredIds">已发现（声明过）的工具 ID 集合。</param>
/// <param name="PinnedIds">固定工具 ID 列表。</param>
public sealed record ToolExposureInput(
    ToolExposurePolicyMode Mode,
    IReadOnlyList<ToolCatalogEntry> Available,
    IReadOnlySet<string> DiscoveredIds,
    IReadOnlyList<string> PinnedIds);

namespace Seeing.Agent.Abstractions.Tools;

/// <summary>
/// 工具暴露策略模式：选择 <see cref="IToolExposurePolicy"/> 的分派依据（与 <see cref="ToolExposureMode"/> 为不同维度，不共用类型）。
/// </summary>
public enum ToolExposurePolicyMode
{
    /// <summary>全部内联：所有可用工具直接进 tools 数组。</summary>
    All,

    /// <summary>自动：由策略按预算/目录规则自动决定内联与延迟。</summary>
    Auto,

    /// <summary>分层：按层级（如 pinned / 已发现 / 其余）分层暴露。</summary>
    Tiered
}

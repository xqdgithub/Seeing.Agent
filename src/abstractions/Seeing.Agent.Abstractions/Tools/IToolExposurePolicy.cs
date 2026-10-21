namespace Seeing.Agent.Abstractions.Tools;

/// <summary>
/// 工具暴露策略端口：由输入快照计算本轮暴露计划。
/// 纯同步单方法实现（无取消令牌、无 DI 编排）；策略实现类位于独立的纯函数策略包。
/// </summary>
public interface IToolExposurePolicy
{
    /// <summary>该策略适用的模式（供选择器按模式分派）。</summary>
    ToolExposurePolicyMode Mode { get; }

    /// <summary>依据输入计算暴露计划。</summary>
    ToolExposurePlan Plan(ToolExposureInput input);
}

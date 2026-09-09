namespace Seeing.Agent.Core.CapabilitySets;

/// <summary>
/// 能力集纯数据定义（仅模块层：模块 id 白名单 + 可选黑名单；不含工作模式字段）。
/// </summary>
public sealed record CapabilitySetDefinition(
    string Name,
    IReadOnlyList<string> Modules,
    IReadOnlyList<string> Disabled);

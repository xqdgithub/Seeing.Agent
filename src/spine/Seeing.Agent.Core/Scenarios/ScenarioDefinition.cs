namespace Seeing.Agent.Core.Scenarios;

/// <summary>
/// 场景预设纯数据定义（模块 / Agent / seams / 工具裁剪均为字符串 id，不引用能力包类型）。
/// </summary>
public sealed record ScenarioDefinition(
    string Name,
    IReadOnlyList<string> Modules,
    string DefaultAgent,
    IReadOnlyDictionary<string, string> Seams,
    IReadOnlyList<string> ToolsDisabled);

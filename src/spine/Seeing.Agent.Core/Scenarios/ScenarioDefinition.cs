namespace Seeing.Agent.Core.Scenarios;

/// <summary>
/// 场景预设纯数据定义（模块 / Agent / seams / 工具裁剪均为字符串 id，不引用能力包类型）。
/// </summary>
/// <param name="PolicyMode">
/// 工具暴露策略模式（场景级；字符串承载，解析推迟到读取处）。null = 未覆盖。
/// 末尾带默认值，保证既有位置构造点向后兼容。
/// </param>
public sealed record ScenarioDefinition(
    string Name,
    IReadOnlyList<string> Modules,
    string DefaultAgent,
    IReadOnlyDictionary<string, string> Seams,
    IReadOnlyList<string> ToolsDisabled,
    string? PolicyMode = null);

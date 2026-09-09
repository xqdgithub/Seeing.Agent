using Seeing.Agent.Core.CapabilitySets;

namespace Seeing.Agent.Core.Scenarios;

/// <summary>
/// 内置场景预设（工作模式）。模块列表编译期引用 <see cref="BuiltInCapabilitySets"/> 常量，禁止复制数组。
/// </summary>
public static class BuiltInScenarios
{
    private static readonly IReadOnlyDictionary<string, string> s_defaultSeams =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["executionWorld"] = "io.local",
        };

    private static readonly IReadOnlyList<string> s_noToolsDisabled = Array.Empty<string>();

    public static ScenarioDefinition Minimal { get; } = new(
        Name: "minimal",
        Modules: BuiltInCapabilitySets.Minimal,
        DefaultAgent: "general",
        Seams: s_defaultSeams,
        ToolsDisabled: s_noToolsDisabled);

    public static ScenarioDefinition Code { get; } = new(
        Name: "code",
        Modules: BuiltInCapabilitySets.Code,
        DefaultAgent: "build",
        Seams: s_defaultSeams,
        ToolsDisabled: s_noToolsDisabled);

    public static ScenarioDefinition Work { get; } = new(
        Name: "work",
        Modules: BuiltInCapabilitySets.Work,
        DefaultAgent: "general",
        Seams: s_defaultSeams,
        ToolsDisabled: s_noToolsDisabled);

    public static ScenarioDefinition Research { get; } = new(
        Name: "research",
        Modules: BuiltInCapabilitySets.Research,
        DefaultAgent: "explore",
        Seams: s_defaultSeams,
        ToolsDisabled: s_noToolsDisabled);

    public static ScenarioDefinition Full { get; } = new(
        Name: "full",
        Modules: BuiltInCapabilitySets.Full,
        DefaultAgent: "build",
        Seams: s_defaultSeams,
        ToolsDisabled: s_noToolsDisabled);

    /// <summary>按名称查找内置场景；未知名称返回 null。</summary>
    public static ScenarioDefinition? TryGet(string name) => name switch
    {
        "minimal" => Minimal,
        "code" => Code,
        "work" => Work,
        "research" => Research,
        "full" => Full,
        _ => null,
    };

    /// <summary>全部内置场景（稳定顺序）。</summary>
    public static IReadOnlyList<ScenarioDefinition> All { get; } =
    [
        Minimal,
        Code,
        Work,
        Research,
        Full,
    ];
}

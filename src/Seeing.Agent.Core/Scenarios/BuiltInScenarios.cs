namespace Seeing.Agent.Core.Scenarios;

/// <summary>
/// 内置场景预设。模块 id 为纯字符串；结算时与宿主 <c>IModuleCatalog.Available</c> 求交。
/// </summary>
public static class BuiltInScenarios
{
    private static readonly IReadOnlyDictionary<string, string> s_defaultSeams =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["executionWorld"] = "io.local",
        };

    private static readonly IReadOnlyList<string> s_noToolsDisabled = Array.Empty<string>();

    private static readonly IReadOnlyList<string> s_minimalModules =
    [
        "io.local",
        "agents.builtin",
        "llm.openai",
        "basic",
    ];

    private static readonly IReadOnlyList<string> s_codeModules =
    [
        "io.local",
        "agents.builtin",
        "llm.openai",
        "basic",
        "filesystem",
        "shell",
        "git",
        "subagent",
    ];

    private static readonly IReadOnlyList<string> s_workModules =
    [
        "io.local",
        "agents.builtin",
        "llm.openai",
        "basic",
        "filesystem",
        "web",
        "memory",
        "scheduler",
    ];

    private static readonly IReadOnlyList<string> s_researchModules =
    [
        "io.local",
        "agents.builtin",
        "llm.openai",
        "basic",
        "web",
        "memory",
    ];

    /// <summary>
    /// 全部已知能力模块 id（含 Scenario 引用与已落地的 skills/mcp/llm.anthropic 等）。
    /// </summary>
    private static readonly IReadOnlyList<string> s_fullModules =
    [
        "io.local",
        "agents.builtin",
        "llm.openai",
        "llm.anthropic",
        "basic",
        "filesystem",
        "shell",
        "git",
        "subagent",
        "web",
        "memory",
        "scheduler",
        "skills",
        "mcp",
    ];

    public static ScenarioDefinition Minimal { get; } = new(
        Name: "minimal",
        Modules: s_minimalModules,
        DefaultAgent: "general",
        Seams: s_defaultSeams,
        ToolsDisabled: s_noToolsDisabled);

    public static ScenarioDefinition Code { get; } = new(
        Name: "code",
        Modules: s_codeModules,
        DefaultAgent: "build",
        Seams: s_defaultSeams,
        ToolsDisabled: s_noToolsDisabled);

    public static ScenarioDefinition Work { get; } = new(
        Name: "work",
        Modules: s_workModules,
        DefaultAgent: "general",
        Seams: s_defaultSeams,
        ToolsDisabled: s_noToolsDisabled);

    public static ScenarioDefinition Research { get; } = new(
        Name: "research",
        Modules: s_researchModules,
        DefaultAgent: "explore",
        Seams: s_defaultSeams,
        ToolsDisabled: s_noToolsDisabled);

    public static ScenarioDefinition Full { get; } = new(
        Name: "full",
        Modules: s_fullModules,
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

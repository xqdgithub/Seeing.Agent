namespace Seeing.Agent.Core.Scenarios;

/// <summary>
/// 内置工作模式。只保留 <c>full</c>：模块列表为 <see cref="AllModules"/>，
/// 会话结算时展开为当前进程已启用的全部模块，不把某一天的勾选结果冻成清单。
/// </summary>
public static class BuiltInScenarios
{
    /// <summary>模块通配。出现在场景 <c>Modules</c> 中时表示不收窄，取全部 bootEnabled。</summary>
    public const string AllModules = "*";

    private static readonly IReadOnlyDictionary<string, string> s_defaultSeams =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["executionWorld"] = "io.local",
        };

    private static readonly IReadOnlyList<string> s_noToolsDisabled = Array.Empty<string>();

    private static readonly IReadOnlyList<string> s_allModules = [AllModules];

    /// <summary>全部模块（默认 Agent 为 build）。</summary>
    public static ScenarioDefinition Full { get; } = new(
        Name: "full",
        Modules: s_allModules,
        DefaultAgent: "build",
        Seams: s_defaultSeams,
        ToolsDisabled: s_noToolsDisabled);

    /// <summary>按名称查找内置场景；未知名称返回 null。</summary>
    public static ScenarioDefinition? TryGet(string name) =>
        string.Equals(name, Full.Name, StringComparison.OrdinalIgnoreCase) ? Full : null;

    /// <summary>全部内置场景（稳定顺序）。</summary>
    public static IReadOnlyList<ScenarioDefinition> All { get; } = [Full];

    /// <summary>模块列表是否声明了 <see cref="AllModules"/>。</summary>
    public static bool IncludesAllModules(IEnumerable<string>? modules)
    {
        if (modules is null)
            return false;

        foreach (var module in modules)
        {
            if (string.Equals(module?.Trim(), AllModules, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}

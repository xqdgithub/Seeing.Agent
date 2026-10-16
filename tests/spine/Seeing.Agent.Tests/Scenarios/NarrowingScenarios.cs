using Seeing.Agent.Core.Scenarios;

namespace Seeing.Agent.Tests.Scenarios;

/// <summary>
/// 已从内置工作模式移除的 code/work 等名称。测试用它们锁定「自定义场景仍可收窄」，
/// 不代表产品仍然内置这些场景。
/// </summary>
internal static class NarrowingScenarios
{
    private static readonly IReadOnlyDictionary<string, string> Seams =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["executionWorld"] = "io.local",
        };

    private static readonly IReadOnlyList<string> None = [];

    public static ScenarioDefinition? Resolve(string name) => name switch
    {
        "minimal" => Def("minimal", ["io.local", "agents.builtin", "llm.openai", "basic"], "general"),
        "code" => Def("code",
            ["io.local", "agents.builtin", "llm.openai", "basic", "filesystem", "shell", "git", "subagent"],
            "build"),
        "work" => Def("work",
            ["io.local", "agents.builtin", "llm.openai", "basic", "filesystem", "web", "memory", "scheduler"],
            "general"),
        "research" => Def("research",
            ["io.local", "agents.builtin", "llm.openai", "basic", "web", "memory"],
            "explore"),
        _ => BuiltInScenarios.TryGet(name),
    };

    private static ScenarioDefinition Def(string name, string[] modules, string agent) =>
        new(name, modules, agent, Seams, None);
}

internal sealed class DelegatingScenarioCatalog(Func<string, ScenarioDefinition?> resolve) : IScenarioCatalog
{
    public IReadOnlyList<ScenarioDefinition> ListAll() => [];

    public ScenarioDefinition? Get(string name) =>
        string.IsNullOrWhiteSpace(name) ? null : resolve(name.Trim());

    public bool IsBuiltInName(string name) =>
        !string.IsNullOrWhiteSpace(name) && BuiltInScenarios.TryGet(name.Trim()) is not null;

    public bool HasConfiguredOverride(string name) => false;
}

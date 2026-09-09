using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Core.Scenarios;
using Seeing.Session.Core;

namespace Seeing.Agent.Hosting.Execution;

/// <summary>
/// 会话级结算快照（在 <see cref="ExecutionJobService"/> 进入执行前计算，本轮不变）。
/// </summary>
internal sealed class SessionSettlementSnapshot
{
    /// <summary>解析后的会话 scenario 名（可能为 null）。</summary>
    public required string? ScenarioName { get; init; }

    /// <summary>会话启用模块（已与进程级 bootEnabled 求交，只能收窄）。</summary>
    public required IReadOnlyList<string> EnabledModules { get; init; }

    /// <summary>工具层：模块 ProvidedTools − Tools.Disabled*。</summary>
    public required IReadOnlyList<string> SettledToolIds { get; init; }
}

/// <summary>
/// 会话级结算：两层分离——模块层只求交 module id；工具层再展开 ProvidedTools 并扣 Tools.Disabled。
/// </summary>
/// <remarks>
/// 模块层：
/// <c>sessionScenario = session.Scenario ?? 进程默认工作模式</c>；
/// <c>sessionModules = scenario.Modules ∩ catalog.Enabled(=bootEnabled)</c>；
/// 若有 <c>session.ScenarioOverride.Modules.Enabled</c> 则以其为候选再 ∩ bootEnabled（不能超 boot）。
/// 工具层（模块层之后）：
/// <c>settledToolIds = ⋃ sessionModules.ProvidedTools − scenario.Tools.Disabled − override.Tools.Disabled − Modules.Tools.Disabled</c>。
/// <c>Tools.Disabled</c> 禁止写入模块层公式。
/// </remarks>
internal static class SessionSettlement
{
    public static SessionSettlementSnapshot Compute(
        SessionData session,
        IModuleCatalog catalog,
        string? processScenario,
        IReadOnlyList<string>? userToolsDisabled,
        Func<string, ScenarioDefinition?>? resolveScenario = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(catalog);

        // 默认 BuiltIn-only；生产路径应传入 IScenarioCatalog.Get（configured ∪ built-in）
        resolveScenario ??= static name => BuiltInScenarios.TryGet(name);

        var bootEnabled = new HashSet<string>(catalog.Enabled, StringComparer.OrdinalIgnoreCase);
        var availableById = catalog.Available.ToDictionary(
            d => d.Id,
            d => d,
            StringComparer.OrdinalIgnoreCase);

        var scenarioName = session.ResolveScenario(processScenario);
        var scenario = string.IsNullOrWhiteSpace(scenarioName)
            ? null
            : resolveScenario(scenarioName.Trim());

        // —— 模块层：只操作 module id，不应用 Tools.Disabled ——
        // sessionModules = scenario.Modules ∩ bootEnabled
        // 有 override 时再 ∩ override.Modules.Enabled（仍 ⊆ bootEnabled）
        var scenarioModules = scenario?.Modules ?? Array.Empty<string>();
        var sessionBase = Intersect(scenarioModules, bootEnabled);

        var enabledModules = (session.ScenarioOverride?.Modules?.Enabled is { } overrideEnabled
                ? Intersect(overrideEnabled, new HashSet<string>(sessionBase, StringComparer.OrdinalIgnoreCase))
                : sessionBase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // —— 工具层：展开 ProvidedTools，再扣各类 Tools.Disabled ——
        var toolIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var moduleId in enabledModules)
        {
            if (!availableById.TryGetValue(moduleId, out var descriptor))
                continue;

            foreach (var toolId in descriptor.ProvidedTools)
            {
                if (!string.IsNullOrWhiteSpace(toolId))
                    toolIds.Add(toolId.Trim());
            }
        }

        RemoveDisabled(toolIds, scenario?.ToolsDisabled);
        RemoveDisabled(toolIds, session.ScenarioOverride?.Tools?.Disabled);
        RemoveDisabled(toolIds, userToolsDisabled);

        return new SessionSettlementSnapshot
        {
            ScenarioName = scenarioName,
            EnabledModules = enabledModules,
            SettledToolIds = toolIds
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private static List<string> Intersect(
        IEnumerable<string> ids,
        HashSet<string> bootEnabled)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in ids)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var id = raw.Trim();
            if (!bootEnabled.Contains(id))
                continue;
            if (seen.Add(id))
                result.Add(id);
        }

        return result;
    }

    private static void RemoveDisabled(HashSet<string> toolIds, IEnumerable<string>? disabled)
    {
        if (disabled is null)
            return;

        foreach (var raw in disabled)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            toolIds.Remove(raw.Trim());
        }
    }
}

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

    /// <summary>会话启用模块（已与进程级 enabled 求交，只能收窄）。</summary>
    public required IReadOnlyList<string> EnabledModules { get; init; }

    /// <summary>层1∩层2 已结算 tool id（模块 ProvidedTools − disabled）。</summary>
    public required IReadOnlyList<string> SettledToolIds { get; init; }
}

/// <summary>
/// 会话级结算：只收窄进程级已启用模块，再展开为 tool id 并扣除 tools.disabled。
/// </summary>
/// <remarks>
/// 公式：
/// <c>sessionScenario = session.Scenario ?? 进程级 scenario</c>；
/// <c>sessionBase = sessionScenario.modules ∩ 进程级 enabled</c>；
/// <c>enabledModules = (session.modules.enabled ?? sessionBase) ∩ 进程级 enabled</c>；
/// <c>settledTools = enabledModules.ProvidedTools − session.tools.disabled − 用户 tools.disabled − scenario.ToolsDisabled</c>。
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

        resolveScenario ??= static name => BuiltInScenarios.TryGet(name);

        var processEnabled = new HashSet<string>(catalog.Enabled, StringComparer.OrdinalIgnoreCase);
        var availableById = catalog.Available.ToDictionary(
            d => d.Id,
            d => d,
            StringComparer.OrdinalIgnoreCase);

        var scenarioName = session.ResolveScenario(processScenario);
        var scenario = string.IsNullOrWhiteSpace(scenarioName)
            ? null
            : resolveScenario(scenarioName.Trim());

        var scenarioModules = scenario?.Modules ?? Array.Empty<string>();
        var sessionBase = Intersect(scenarioModules, processEnabled);

        IReadOnlyList<string> candidateSource = session.ScenarioOverride?.Modules?.Enabled is { } overrideEnabled
            ? overrideEnabled
            : sessionBase;

        var enabledModules = Intersect(candidateSource, processEnabled)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

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
        HashSet<string> processEnabled)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in ids)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var id = raw.Trim();
            if (!processEnabled.Contains(id))
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

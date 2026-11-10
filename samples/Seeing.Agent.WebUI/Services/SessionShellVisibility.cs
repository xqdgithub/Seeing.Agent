using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Core.Scenarios;
using Seeing.Agent.Core.Ui;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 会话壳可见性：场景解析、默认 Agent、插槽过滤（会话级 Scenario）；
/// 侧栏导航过滤走进程级（与会话壳分离）。
/// <para>
/// <b>本类已降级为薄封装</b>：模块启用判定委托 <see cref="UiVisibility.AreRequirementsEnabled"/>，
/// 场景→模块集解析委托 <see cref="UiScenarioGate"/>。判定只在 Core 有一份实现。
/// </para>
/// </summary>
public static class SessionShellVisibility
{
    /// <summary>有效场景名：会话级优先，否则进程级。</summary>
    public static string? ResolveEffectiveScenario(string? sessionScenario, string? processScenario) =>
        UiScenarioGate.ResolveEffective(sessionScenario, processScenario);

    /// <summary>取场景预设的 defaultAgent；未知场景返回 null。</summary>
    public static string? ResolveDefaultAgent(
        string? effectiveScenario,
        IScenarioCatalog? catalog = null)
    {
        if (string.IsNullOrWhiteSpace(effectiveScenario))
            return null;
        var name = effectiveScenario.Trim();
        return catalog?.Get(name)?.DefaultAgent
               ?? BuiltInScenarios.TryGet(name)?.DefaultAgent;
    }

    /// <summary>
    /// 会话壳插槽过滤：模块须进程启用，且须落在会话有效场景模块集内。
    /// （插槽贡献的 Scenarios 白名单已随 A2 清理删除，故此处不再传场景白名单。）
    /// </summary>
    public static IReadOnlyList<LegacySlotContribution> FilterSlotsForSession(
        IEnumerable<LegacySlotContribution> slots,
        string slotName,
        IModuleCatalog? catalog,
        string? sessionScenario,
        string? processScenario,
        IScenarioCatalog? scenarioCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotName);

        var effective = ResolveEffectiveScenario(sessionScenario, processScenario);
        var sessionModules = UiScenarioGate.Resolve(effective, scenarioCatalog);

        return slots
            .Where(s => string.Equals(s.Name, slotName, StringComparison.OrdinalIgnoreCase))
            .Where(s => IsVisible(s.Requires, scenarios: null, catalog, effective, sessionModules))
            .ToArray();
    }

    /// <summary>贡献是否可见（requires + scenarios + 可选会话模块闸）。</summary>
    public static bool IsVisible(
        IReadOnlyList<string> requires,
        IReadOnlyList<string>? scenarios,
        IModuleCatalog? catalog,
        string? effectiveScenario,
        IReadOnlySet<string>? sessionModuleGate)
    {
        ArgumentNullException.ThrowIfNull(requires);

        if (!UiVisibility.AreRequirementsEnabled(requires, catalog))
            return false;

        if (sessionModuleGate is not null && requires.Count > 0)
        {
            foreach (var id in requires)
            {
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                if (!sessionModuleGate.Contains(id.Trim()))
                    return false;
            }
        }

        if (scenarios is null || scenarios.Count == 0)
            return true;

        if (string.IsNullOrWhiteSpace(effectiveScenario))
            return false;

        foreach (var name in scenarios)
        {
            if (string.Equals(name, effectiveScenario, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

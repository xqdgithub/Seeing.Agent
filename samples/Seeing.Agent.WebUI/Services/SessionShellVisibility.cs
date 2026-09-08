using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Core.Scenarios;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 会话壳可见性：场景解析、默认 Agent、插槽过滤（会话级 Scenario）；
/// 侧栏导航过滤走进程级（与会话壳分离）。
/// </summary>
public static class SessionShellVisibility
{
    /// <summary>有效场景名：会话级优先，否则进程级。</summary>
    public static string? ResolveEffectiveScenario(string? sessionScenario, string? processScenario) =>
        string.IsNullOrWhiteSpace(sessionScenario)
            ? (string.IsNullOrWhiteSpace(processScenario) ? null : processScenario.Trim())
            : sessionScenario.Trim();

    /// <summary>取场景预设的 defaultAgent；未知场景返回 null。</summary>
    public static string? ResolveDefaultAgent(string? effectiveScenario)
    {
        if (string.IsNullOrWhiteSpace(effectiveScenario))
            return null;
        return BuiltInScenarios.TryGet(effectiveScenario.Trim())?.DefaultAgent;
    }

    /// <summary>
    /// 进程级导航过滤：只看 <see cref="IModuleCatalog.IsEnabled"/> + 贡献 Scenarios 对进程场景。
    /// 会话级 Scenario 变更不得影响此结果。
    /// </summary>
    public static IReadOnlyList<NavContribution> FilterNavForProcess(
        IEnumerable<NavContribution> items,
        IModuleCatalog? catalog,
        string? processScenario)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items
            .Where(n => IsVisible(n.Requires, n.Scenarios, catalog, processScenario, sessionModuleGate: null))
            .ToArray();
    }

    /// <summary>
    /// 会话壳插槽过滤：模块须进程启用，且须落在会话有效场景模块集内；
    /// 另受贡献 <see cref="SlotContribution.Scenarios"/> 白名单约束。
    /// </summary>
    public static IReadOnlyList<SlotContribution> FilterSlotsForSession(
        IEnumerable<SlotContribution> slots,
        string slotName,
        IModuleCatalog? catalog,
        string? sessionScenario,
        string? processScenario)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotName);

        var effective = ResolveEffectiveScenario(sessionScenario, processScenario);
        var sessionModules = ResolveScenarioModuleSet(effective);

        return slots
            .Where(s => string.Equals(s.Name, slotName, StringComparison.OrdinalIgnoreCase))
            .Where(s => IsVisible(s.Requires, s.Scenarios, catalog, effective, sessionModules))
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

        if (!AreRequirementsEnabled(requires, catalog))
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

    private static bool AreRequirementsEnabled(IReadOnlyList<string> requires, IModuleCatalog? catalog)
    {
        if (requires.Count == 0 || catalog is null)
            return true;

        foreach (var id in requires)
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;
            if (!catalog.IsEnabled(id))
                return false;
        }

        return true;
    }

    private static IReadOnlySet<string>? ResolveScenarioModuleSet(string? effectiveScenario)
    {
        if (string.IsNullOrWhiteSpace(effectiveScenario))
            return null;

        var def = BuiltInScenarios.TryGet(effectiveScenario.Trim());
        if (def is null)
            return null;

        return new HashSet<string>(def.Modules, StringComparer.OrdinalIgnoreCase);
    }
}

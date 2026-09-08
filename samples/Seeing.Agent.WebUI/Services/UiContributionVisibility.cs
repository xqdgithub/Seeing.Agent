using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Modules;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 侧栏 / 设置页贡献可见性：只读 <see cref="IModuleCatalog"/> + 进程级 Scenario（不含会话 Scenario）。
/// </summary>
public static class UiContributionVisibility
{
    /// <summary>
    /// 解析进程级场景名：seeing.json <c>scenario</c>，否则 Host Shape 默认。
    /// </summary>
    public static string? ResolveProcessScenario(
        SeeingAgentOptions? options,
        ProcessSettlementOptions? settlementOptions = null)
    {
        if (!string.IsNullOrWhiteSpace(options?.Scenario))
            return options.Scenario.Trim();
        if (!string.IsNullOrWhiteSpace(settlementOptions?.HostDefaultScenario))
            return settlementOptions.HostDefaultScenario.Trim();
        return null;
    }

    /// <summary>
    /// 侧栏可见 Nav：排除参数化路由、子路径，并按 Requires + Scenarios 过滤。
    /// </summary>
    public static IReadOnlyList<NavContribution> FilterSidebarNav(
        IEnumerable<NavContribution> navItems,
        IModuleCatalog? catalog,
        string? processScenario)
    {
        ArgumentNullException.ThrowIfNull(navItems);

        var candidates = navItems
            .Where(n => !string.IsNullOrWhiteSpace(n.Route))
            .Where(n => n.Route.IndexOf('{') < 0)
            .Where(n => AreRequirementsEnabled(n.Requires, catalog))
            .Where(n => MatchesProcessScenario(n.Scenarios, processScenario))
            .ToList();

        // 有更短前缀路由时隐藏子路径（如 /memory 存在则不显示 /memory/settings）
        return candidates
            .Where(n => !HasShorterPrefix(n.Route, candidates))
            .ToList();
    }

    /// <summary>设置卡片可见性：仅 Requires ∩ 进程级启用模块。</summary>
    public static IReadOnlyList<SettingsCardContribution> FilterSettingsCards(
        IEnumerable<SettingsCardContribution> cards,
        IModuleCatalog? catalog)
    {
        ArgumentNullException.ThrowIfNull(cards);
        return cards
            .Where(c => AreRequirementsEnabled(c.Requires, catalog))
            .ToList();
    }

    internal static bool AreRequirementsEnabled(IReadOnlyList<string> requires, IModuleCatalog? catalog)
    {
        if (requires.Count == 0)
            return true;
        if (catalog is null)
            return false;

        foreach (var id in requires)
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;
            if (!catalog.IsEnabled(id))
                return false;
        }

        return true;
    }

    internal static bool MatchesProcessScenario(IReadOnlyList<string>? scenarios, string? processScenario)
    {
        if (scenarios is null || scenarios.Count == 0)
            return true;
        if (string.IsNullOrWhiteSpace(processScenario))
            return false;

        foreach (var s in scenarios)
        {
            if (string.Equals(s, processScenario, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool HasShorterPrefix(string route, IReadOnlyList<NavContribution> candidates)
    {
        var normalized = NormalizeRoute(route);
        foreach (var other in candidates)
        {
            var prefix = NormalizeRoute(other.Route);
            if (prefix.Length >= normalized.Length)
                continue;
            if (normalized.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string NormalizeRoute(string route)
    {
        if (string.IsNullOrWhiteSpace(route))
            return "/";
        var path = route.Trim();
        if (!path.StartsWith('/'))
            path = "/" + path;
        if (path.Length > 1)
            path = path.TrimEnd('/');
        return path;
    }
}

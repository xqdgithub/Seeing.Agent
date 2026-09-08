using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.WebUI.Pages;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 将已登记模块的 Nav 路由绑定到现有 Page 组件类型（T1；T3 后可由模块直接携带 ComponentType）。
/// </summary>
public static class ModulePageRouteBinder
{
    private static readonly Dictionary<string, Type> s_routeToPage =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["/memory"] = typeof(MemoryPage),
            ["/memory/settings"] = typeof(MemorySettingsPage),
            ["/memory/browse"] = typeof(MemoryBrowsePage),
            ["/memory/graph"] = typeof(MemoryGraphPage),
            ["/memory/stats"] = typeof(MemoryStatsPage),
            ["/memory/detail"] = typeof(MemoryDetailPage),
            ["/cron-jobs"] = typeof(CronJobsPage),
            ["/heartbeat"] = typeof(HeartbeatPage),
            ["/mcp"] = typeof(McpPage),
            ["/acp"] = typeof(AcpPage),
            ["/gateway"] = typeof(GatewayPage),
            ["/gateway-clients"] = typeof(GatewayClientsPage),
            ["/skills"] = typeof(SkillsPage),
            ["/tools"] = typeof(ToolsPage),
        };

    /// <summary>
    /// 用带 <see cref="NavContribution.ComponentType"/> 的贡献覆盖同模块登记（保留 Settings/Slot/Renderer）。
    /// </summary>
    public static void BindExistingPages(IUiContributionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        foreach (var moduleId in registry.NavItems
                     .SelectMany(n => n.Requires)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .ToArray())
        {
            var navs = registry.NavItems
                .Where(n => n.Requires.Any(r => string.Equals(r, moduleId, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (navs.Count == 0)
                continue;

            var settings = registry.SettingsCards
                .Where(s => s.Requires.Any(r => string.Equals(r, moduleId, StringComparison.OrdinalIgnoreCase)))
                .Cast<object>();
            var slots = registry.Slots
                .Where(s => s.Requires.Any(r => string.Equals(r, moduleId, StringComparison.OrdinalIgnoreCase)))
                .Cast<object>();

            var enrichedNav = navs.Select(n =>
            {
                s_routeToPage.TryGetValue(n.Route, out var pageType);
                return (object)(n with { ComponentType = pageType ?? n.ComponentType });
            });

            registry.Register(new BoundUiContribution(
                moduleId,
                enrichedNav.Concat(settings).Concat(slots).ToList()));
        }
    }

    private sealed class BoundUiContribution(string moduleId, IReadOnlyList<object> items) : IUiContribution
    {
        public string ModuleId { get; } = moduleId;
        public IReadOnlyList<object> Contribute() => items;
    }
}

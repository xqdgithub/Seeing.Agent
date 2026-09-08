using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.WebUI.Pages;

namespace Seeing.Agent.WebUI.Services;

/// <summary>已知模块页路由绑定（模板 → 组件/模块）。</summary>
public sealed record ModuleRouteBinding(
    string Template,
    Type ComponentType,
    string ModuleId,
    string Title,
    string Icon);

/// <summary>
/// 将已登记模块的 Nav 路由绑定到 Page 组件类型；并提供已知模块路由表供未启用直链提示。
/// </summary>
public static class ModulePageRouteBinder
{
    /// <summary>能力模块页（含详情子路由）。壳页见 <see cref="CoreShellUiContribution"/>。</summary>
    public static IReadOnlyList<ModuleRouteBinding> KnownModuleRoutes { get; } =
    [
        new("/memory", typeof(MemoryPage), "memory", "记忆", "database"),
        new("/memory/settings", typeof(MemorySettingsPage), "memory", "记忆设置", "setting"),
        new("/memory/browse", typeof(MemoryBrowsePage), "memory", "记忆浏览", "folder"),
        new("/memory/graph", typeof(MemoryGraphPage), "memory", "知识图谱", "apartment"),
        new("/memory/stats", typeof(MemoryStatsPage), "memory", "记忆统计", "bar-chart"),
        new("/memory/detail/{*FilePath}", typeof(MemoryDetailPage), "memory", "记忆详情", "file"),
        new("/cron-jobs", typeof(CronJobsPage), "scheduler", "定时任务", "clock-circle"),
        new("/cron-jobs/{JobId}", typeof(JobDetailPage), "scheduler", "任务详情", "clock-circle"),
        new("/heartbeat", typeof(HeartbeatPage), "scheduler", "心跳", "heart"),
        new("/mcp", typeof(McpPage), "mcp", "MCP", "api"),
        new("/acp", typeof(AcpPage), "acp", "ACP", "robot"),
        new("/gateway", typeof(GatewayPage), "gateway", "Gateway", "global"),
        new("/gateway-clients", typeof(GatewayClientsPage), "gateway", "Gateway 客户端", "api"),
        new("/skills", typeof(SkillsPage), "skills", "技能", "star"),
        new("/skills/create", typeof(SkillCreatePage), "skills", "创建技能", "star"),
        new("/skills/{SkillName}", typeof(SkillDetailPage), "skills", "技能详情", "star"),
        new("/tools", typeof(ToolsPage), "basic", "工具", "tool"),
    ];

    /// <summary>
    /// 用带 <see cref="NavContribution.ComponentType"/> 的贡献覆盖同模块登记（保留 Settings/Slot/Renderer）。
    /// </summary>
    public static void BindExistingPages(IUiContributionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        foreach (var moduleId in KnownModuleRoutes
                     .Select(r => r.ModuleId)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .ToArray())
        {
            var hasModule = registry.NavItems.Any(n =>
                n.Requires.Any(r => string.Equals(r, moduleId, StringComparison.OrdinalIgnoreCase)));
            if (!hasModule)
                continue;

            var existingNav = registry.NavItems
                .Where(n => n.Requires.Any(r => string.Equals(r, moduleId, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var settings = registry.SettingsCards
                .Where(s => s.Requires.Any(r => string.Equals(r, moduleId, StringComparison.OrdinalIgnoreCase)))
                .Cast<object>();
            var slots = registry.Slots
                .Where(s => s.Requires.Any(r => string.Equals(r, moduleId, StringComparison.OrdinalIgnoreCase)))
                .Cast<object>();

            var byRoute = existingNav.ToDictionary(
                n => n.Route,
                StringComparer.OrdinalIgnoreCase);

            var enriched = new List<object>();
            foreach (var known in KnownModuleRoutes.Where(k =>
                         string.Equals(k.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase)))
            {
                if (byRoute.TryGetValue(known.Template, out var existing))
                {
                    enriched.Add(existing with { ComponentType = known.ComponentType });
                    byRoute.Remove(known.Template);
                }
                else
                {
                    enriched.Add(new NavContribution(
                        known.Template,
                        known.Title,
                        known.Icon,
                        [moduleId],
                        ComponentType: known.ComponentType));
                }
            }

            // 保留模块贡献了但不在 Known 表中的 Nav
            enriched.AddRange(byRoute.Values.Select(n =>
            {
                var type = KnownModuleRoutes
                    .FirstOrDefault(k => string.Equals(k.Template, n.Route, StringComparison.OrdinalIgnoreCase))
                    ?.ComponentType;
                return (object)(n with { ComponentType = type ?? n.ComponentType });
            }));

            registry.Register(new BoundUiContribution(
                moduleId,
                enriched.Concat(settings).Concat(slots).ToList()));
        }
    }

    /// <summary>已知模块路由表匹配（用于未启用直链）。</summary>
    public static bool TryMatchKnown(
        string path,
        out ModuleRouteBinding binding,
        out IReadOnlyDictionary<string, object?> parameters)
    {
        ModuleRouteBinding? best = null;
        IReadOnlyDictionary<string, object?>? bestParams = null;
        var bestScore = int.MinValue;

        foreach (var candidate in KnownModuleRoutes)
        {
            if (!ModuleRouteResolver.TryMatchTemplate(
                    candidate.Template, path, out var parms, out var score))
                continue;
            if (score <= bestScore)
                continue;
            bestScore = score;
            best = candidate;
            bestParams = parms;
        }

        if (best is null)
        {
            binding = null!;
            parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            return false;
        }

        binding = best;
        parameters = bestParams!;
        return true;
    }

    private sealed class BoundUiContribution(string moduleId, IReadOnlyList<object> items) : IUiContribution
    {
        public string ModuleId { get; } = moduleId;
        public IReadOnlyList<object> Contribute() => items;
    }
}

using Seeing.Agent.WebUI.Components;
using Seeing.Agent.WebUI.Components.Scheduler;
using Seeing.Agent.WebUI.Components.Settings;
using Seeing.Agent.WebUI.Pages;using SessionPage = global::Seeing.Agent.WebUI.Pages.Session;
using IndexPage = global::Seeing.Agent.WebUI.Pages.Index;

namespace Seeing.Agent.WebUI.Shell;

/// <summary>
/// WebUI 内建视图登记表（<c>ViewKey → 组件</c>）。
/// <para>
/// <b>为什么视图表在宿主层</b>：能力包受依赖方向限制，不可能持有 WebUI 的组件类型；
/// 契约只承诺 <c>ViewKey</c> 字符串。这也让同一视图可以被多条路由复用
/// （如 <c>/session</c> 与 <c>/session/{SessionId}</c> 共用 <see cref="SessionPage"/>）。
/// </para>
/// <para>
/// 命名约定：<c>ViewKey</c> 与贡献的 <c>Id</c> 相同（便于守门与人工核对），
/// 仅当多条贡献共用同一组件时 ViewKey 才重复出现（注册表按 key 去重）。
/// </para>
/// </summary>
public static class WebUiViews
{
    /// <summary>登记全部宿主与模块页 + 设置卡。</summary>
    /// <param name="resolver">目标解析器。</param>
    public static void RegisterBuiltIns(WebUiViewResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        // ── 壳层页面（host.webui）──
        // 全限定：Index 与 System.Index 歧义；Session 与 Seeing.Session 命名空间冲突
        resolver.Register("page.home", typeof(IndexPage));
        resolver.Register("page.home.classic", typeof(Home));
        resolver.Register("page.workspaces", typeof(WorkspacesPage));
        resolver.Register("page.session", typeof(SessionPage));
        resolver.Register("page.conference", typeof(Conference));
        resolver.Register("page.sessions", typeof(SessionsPage));
        resolver.Register("page.agents", typeof(AgentsPage));
        resolver.Register("page.agent-config", typeof(AgentConfigPage));
        resolver.Register("page.models", typeof(ModelsPage));
        resolver.Register("page.security", typeof(SecurityPage));
        resolver.Register("page.settings", typeof(Settings));

        // ── 能力模块页面（15 条路由，由各模块自己声明）──
        resolver.Register("page.memory", typeof(MemoryPage));
        resolver.Register("page.memory.graph", typeof(MemoryGraphPage));
        resolver.Register("page.memory.detail", typeof(MemoryDetailPage));
        resolver.Register("page.cron", typeof(CronJobsPage));
        resolver.Register("page.cron.detail", typeof(JobDetailPage));
        resolver.Register("page.heartbeat", typeof(HeartbeatPage));
        resolver.Register("page.mcp", typeof(McpPage));
        resolver.Register("page.acp", typeof(AcpPage));
        resolver.Register("page.gateway", typeof(GatewayPage));
        resolver.Register("page.gateway.clients", typeof(GatewayClientsPage));
        resolver.Register("page.skills", typeof(SkillsPage));
        resolver.Register("page.skills.create", typeof(SkillCreatePage));
        resolver.Register("page.skills.detail", typeof(SkillDetailPage));
        resolver.Register("page.tools", typeof(ToolsPage));
        resolver.Register("page.reflection", typeof(ReflectionRunsPage));

        // ── 设置页卡片（4 张，宿主代模块声明）──
        resolver.Register("settings.boot", typeof(BootCapabilitySettingsCard));
        resolver.Register("settings.scenarios", typeof(ScenariosSettingsCard));
        resolver.Register("settings.scheduler", typeof(SchedulerSettingsCard));
        resolver.Register("settings.memory", typeof(MemorySettingsCard));

        // ── 插槽（文件面板，宿主代 files.workspace 声明）──
        resolver.Register(WebUiViewKeys.SessionFilePanel, typeof(SessionFilePanel));
    }
}

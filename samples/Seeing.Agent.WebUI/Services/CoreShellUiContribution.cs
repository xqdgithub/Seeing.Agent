using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.WebUI.Components.Scheduler;
using Seeing.Agent.WebUI.Components.Settings;
using Seeing.Agent.WebUI.Pages;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// WebUI 壳层页面贡献（非能力模块；始终登记，供 ModuleRouter 渲染已去掉 @page 的页面）。
/// </summary>
public sealed class CoreShellUiContribution : IUiContribution
{
    public const string ModuleIdValue = "__webui.shell__";

    /// <inheritdoc />
    public string ModuleId => ModuleIdValue;

    /// <inheritdoc />
    public IReadOnlyList<object> Contribute() =>
    [
        // 纯路由（不进侧栏）— 原 @page 壳页
        // 全限定：Index 与 System.Index 歧义；Session 与 Seeing.Session 命名空间冲突
        new RouteContribution("/", "首页", typeof(global::Seeing.Agent.WebUI.Pages.Index)),
        new RouteContribution("/home", "首页", typeof(Home)),
        new RouteContribution("/session", "会话", typeof(global::Seeing.Agent.WebUI.Pages.Session)),
        new RouteContribution("/session/{SessionId}", "会话", typeof(global::Seeing.Agent.WebUI.Pages.Session)),
        new RouteContribution("/conference", "会议视图", typeof(Conference)),
        new RouteContribution("/conference/{SessionId}", "会议视图", typeof(Conference)),

        // 侧栏 Nav（含 ComponentType，同时进入 Routes）
        new NavContribution("/sessions", "会话", "team", [],
            ComponentType: typeof(SessionsPage),
            Group: NavGroups.Control, GroupIcon: NavGroups.ControlIcon, Order: 10),
        new NavContribution("/agents", "智能体管理", "usergroup-add", [],
            ComponentType: typeof(AgentsPage),
            Group: NavGroups.Settings, GroupIcon: NavGroups.SettingsIcon, Order: 10),
        new NavContribution("/agent-config", "智能体配置", "edit", [],
            ComponentType: typeof(AgentConfigPage),
            Group: NavGroups.Settings, GroupIcon: NavGroups.SettingsIcon, Order: 20),
        new NavContribution("/models", "模型", "cloud-server", [],
            ComponentType: typeof(ModelsPage),
            Group: NavGroups.Settings, GroupIcon: NavGroups.SettingsIcon, Order: 30),
        new NavContribution("/security", "安全", "lock", [],
            ComponentType: typeof(SecurityPage),
            Group: NavGroups.Settings, GroupIcon: NavGroups.SettingsIcon, Order: 80),
        new NavContribution("/settings", "系统设置", "tool", [],
            ComponentType: typeof(Settings),
            Group: NavGroups.Settings, GroupIcon: NavGroups.SettingsIcon, Order: 90),
        new SettingsCardContribution(
            "/settings/scenarios",
            "场景",
            typeof(ScenariosSettingsCard),
            []),
        new SettingsCardContribution(
            "/settings/scheduler",
            "调度器",
            typeof(SchedulerSettingsCard),
            ["scheduler"]),
    ];
}

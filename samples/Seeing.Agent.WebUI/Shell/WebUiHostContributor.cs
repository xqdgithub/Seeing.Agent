using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.WebUI.Services;

namespace Seeing.Agent.WebUI.Shell;

/// <summary>
/// WebUI 壳层贡献（<c>OwnerId = "host.webui"</c>）。
/// <para>
/// <b>壳层不是能力模块</b>：页面与设置卡必须始终可达。绑到模块上会导致用户切换 Boot 能力集后
/// 失去进入设置页的入口（<c>/settings</c> 消失 → 无法自救）。因此这里走
/// <see cref="IUiHostContributor"/>：<c>Requires = []</c>（进程级）+ <c>OwnerId</c> 以 <c>host.</c>
/// 开头，两者缺一都会被注册表拒收。
/// </para>
/// <para>
/// 收集时机：<c>Build()</c> 之后、<c>InitializeSeeingAsync()</c> 之前（与工作区是否选定无关）。
/// </para>
/// </summary>
public sealed class WebUiHostContributor : IUiHostContributor
{
    /// <summary>宿主 owner id（必须以 <c>host.</c> 开头）。</summary>
    public const string HostOwnerId = "host.webui";

    /// <inheritdoc />
    public string OwnerId => HostOwnerId;

    /// <inheritdoc />
    public IReadOnlyList<UiContribution> DescribeUi() =>
    [
        // ── 纯路由（不进侧栏）— 原 @page 壳页 ──
        // 带占位符的路由与它的无参版本共用同一个视图（参数由 UiRouteTemplate 逐字填给 [Parameter]）
        Page("page.home", "/", "首页", "page.home"),
        Page("page.home.classic", "/home", "首页", "page.home.classic"),
        Page("page.workspaces", "/workspaces", "选择工作区", "page.workspaces"),
        Page("page.session", "/session", "会话", "page.session"),
        Page("page.session.detail", "/session/{SessionId}", "会话", "page.session"),
        Page("page.conference", "/conference", "会议视图", "page.conference"),
        Page("page.conference.detail", "/conference/{SessionId}", "会议视图", "page.conference"),

        // ── 侧栏 Nav ──
        Nav("page.sessions", "/sessions", "会话", "team",
            NavGroups.Control, NavGroups.ControlIcon, SidebarProjectionService.GroupOrders.Control, 10),
        Nav("page.agents", "/agents", "智能体管理", "usergroup-add",
            NavGroups.Settings, NavGroups.SettingsIcon, SidebarProjectionService.GroupOrders.Settings, 10),
        Nav("page.agent-config", "/agent-config", "智能体配置", "edit",
            NavGroups.Settings, NavGroups.SettingsIcon, SidebarProjectionService.GroupOrders.Settings, 20),
        Nav("page.models", "/models", "模型", "cloud-server",
            NavGroups.Settings, NavGroups.SettingsIcon, SidebarProjectionService.GroupOrders.Settings, 30),
        Nav("page.security", "/security", "安全", "lock",
            NavGroups.Settings, NavGroups.SettingsIcon, SidebarProjectionService.GroupOrders.Settings, 80),
        Nav("page.settings", "/settings", "系统设置", "tool",
            NavGroups.Settings, NavGroups.SettingsIcon, SidebarProjectionService.GroupOrders.Settings, 90),

        // ── 设置页卡片（宿主代模块声明）──
        // 记忆设置不能当作 Nav 挂在 /memory 下：更短前缀规则会隐藏它的子路由。
        // 设置类内容统一走设置卡片。
        // Order 决定页签顺序（注册表按 Order → Id 排序；都为 0 时会退化成字母序）。
        Card("settings.boot", "启动能力", order: 10),
        Card("settings.scenarios", "工作模式", order: 20),
        Card("settings.scheduler", "调度器", order: 30, Requires: ["scheduler"]),
        Card("settings.memory", "记忆", order: 40, Requires: ["memory"]),
    ];

    private static PageContribution Page(string id, string route, string title, string viewKey) => new()
    {
        Point = WebUiPoints.ShellPage.Id,
        Id = id,
        Route = route,
        Title = title,
        ViewKey = viewKey,
        Requires = [],
    };

    private static PageContribution Nav(
        string id, string route, string title, string icon,
        string group, string groupIcon, int groupOrder, int order) => new()
        {
            Point = WebUiPoints.ShellPage.Id,
            Id = id,
            Route = route,
            Title = title,
            ViewKey = id,
            Requires = [],
            Order = order,
            Nav = new NavPlacement
            {
                Icon = icon,
                Group = group,
                GroupIcon = groupIcon,
                GroupOrder = groupOrder,
            },
        };

    /// <summary>
    /// 设置卡。<paramref name="requires"/> 非空表示「这张卡由宿主代该模块声明」——
    /// 模块停用后卡片消失（与 Nav 的归属语义一致）。
    /// </summary>
    private static SettingsCardContribution Card(
        string id, string title, int order, IReadOnlyList<string>? Requires = null) => new()
        {
            Point = WebUiPoints.ShellSettingsCard.Id,
            Id = id,
            Title = title,
            ViewKey = id,
            Order = order,
            Requires = Requires ?? [],
        };
}

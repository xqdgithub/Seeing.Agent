using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Core.Ui;

namespace Seeing.Agent.WebUI.Services;

/// <summary>侧栏渲染用的导航视图项。</summary>
/// <param name="Key">Ant Menu 的 item key（唯一）。</param>
/// <param name="Route">路由路径。</param>
/// <param name="Title">显示标题。</param>
/// <param name="Icon">已映射的图标名。</param>
/// <param name="Group">分组名；null/空 = 顶层扁平。</param>
/// <param name="GroupIcon">已映射的分组图标名。</param>
/// <param name="Order">同组内排序。</param>
/// <param name="GroupOrder">分组排序。</param>
public sealed record NavView(
    string Key,
    string Route,
    string Title,
    string Icon,
    string? Group,
    string? GroupIcon,
    int Order,
    int GroupOrder);

/// <summary>侧栏分组视图。</summary>
/// <param name="Title">分组标题。</param>
/// <param name="Icon">分组图标（取组内 Order 最前者的非空 GroupIcon）。</param>
/// <param name="Items">组内条目（已排序）。</param>
public sealed record NavGroupView(string Title, string? Icon, IReadOnlyList<NavView> Items)
{
    /// <summary>Ant Menu 的 SubMenu key（分组标题唯一，可直接作 key）。</summary>
    public string Key => Title;
}

/// <summary>侧栏投影结果。</summary>
/// <param name="Visible">可见导航项（已过滤、已排序）。</param>
/// <param name="Ungrouped">无分组项。</param>
/// <param name="Groups">分组项。</param>
public sealed record SidebarProjection(
    IReadOnlyList<NavView> Visible,
    IReadOnlyList<NavView> Ungrouped,
    IReadOnlyList<NavGroupView> Groups);

/// <summary>
/// 侧栏投影（纯 C#，可测）。
/// <para>
/// 从 <c>AppSidebar.razor</c> 提取，<b>行为必须逐条等价</b>：
/// ① 参数化路由不进侧栏；② 更短前缀隐藏子路径；③ 按 <c>GroupOrder</c> 分组
/// （未声明排最后）；④ 组内按 <c>(Order, Title)</c>；⑤ <c>SelectedKey</c> 特例与前缀匹配。
/// </para>
/// <para>
/// 输入必须是<b>已按可见性过滤</b>的条目（<see cref="UiVisibility"/> 的判定需要模块目录，
/// 属于调用方上下文，投影本身不收模块状态）。
/// </para>
/// </summary>
public static class SidebarProjectionService
{
    /// <summary>侧栏「聊天」项的内建 key（对应 <c>AppSidebar.razor</c> 的特例）。</summary>
    public const string ChatKey = "chat";

    /// <summary>内建分组的默认顺序（取代 <c>AppSidebar.razor</c> 里的 preferred 数组）。</summary>
    public static class GroupOrders
    {
        public const int Control = 10;
        public const int Workspace = 20;
        public const int Settings = 30;

        /// <summary>未显式声明 GroupOrder 的分组，排在所有已声明之后。</summary>
        public const int Unspecified = int.MaxValue;
    }

    /// <summary>
    /// 投影侧栏。
    /// </summary>
    /// <param name="entries">已按可见性过滤的页面条目。</param>
    /// <param name="resolveIcon">图标 token → 图标名映射。</param>
    public static SidebarProjection Project(
        IEnumerable<UiContributionEntry> entries,
        Func<string, string>? resolveIcon = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var map = resolveIcon ?? (token => token);

        var navCapable = entries
            .Select(e => e.Contribution)
            .OfType<PageContribution>()
            .Where(p => p.Nav is not null)
            .Where(p => !string.IsNullOrWhiteSpace(p.Route))
            .Where(p => p.Route.IndexOf('{') < 0)          // 规则①：参数化路由不进侧栏
            .ToList();

        var routes = navCapable.Select(p => p.Route).ToList();

        var visible = navCapable
            .Where(p => !HasShorterPrefix(p.Route, routes))   // 规则②：更短前缀隐藏子路径
            .Select(p => new NavView(
                Key: NavKey(p.Route),
                Route: p.Route,
                Title: p.Title,
                Icon: map(p.Nav!.Icon),
                Group: p.Nav.Group,
                GroupIcon: string.IsNullOrWhiteSpace(p.Nav.GroupIcon) ? null : map(p.Nav.GroupIcon),
                Order: p.Order,
                GroupOrder: p.Nav.GroupOrder))
            .OrderBy(v => v.Order)
            .ThenBy(v => v.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var ungrouped = visible
            .Where(v => string.IsNullOrWhiteSpace(v.Group))
            .ToList();

        var groups = visible
            .Where(v => !string.IsNullOrWhiteSpace(v.Group))
            .GroupBy(v => v.Group!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => EffectiveGroupOrder(g))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new NavGroupView(
                Title: g.Key,
                Icon: g.OrderBy(v => v.Order).ThenBy(v => v.Title, StringComparer.OrdinalIgnoreCase)
                        .Select(v => v.GroupIcon)
                        .FirstOrDefault(i => !string.IsNullOrWhiteSpace(i)),
                Items: g.OrderBy(v => v.Order).ThenBy(v => v.Title, StringComparer.OrdinalIgnoreCase)
                        .ToList()))
            .ToList();

        return new SidebarProjection(visible, ungrouped, groups);
    }

    /// <summary>
    /// 计算菜单选中键。
    /// <para>
    /// 与 <c>AppSidebar.razor</c> 的 <c>UpdateSelectedKey</c> 等价：
    /// 空路径 / <c>home</c> / <c>session</c> → chat；否则前缀匹配，未命中回落到首段。
    /// </para>
    /// </summary>
    /// <param name="relativePath">当前路由的相对路径。</param>
    /// <param name="visible"><see cref="SidebarProjection.Visible"/>。</param>
    public static IReadOnlyList<string> SelectedKeys(string? relativePath, IReadOnlyList<NavView> visible)
    {
        ArgumentNullException.ThrowIfNull(visible);

        var path = (relativePath ?? string.Empty).TrimEnd('/');
        var segment = path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

        if (segment is "" or "home" or "session")
            return [ChatKey];

        var match = visible.FirstOrDefault(v =>
        {
            var route = v.Route.Trim('/');
            return path.Equals(route, StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith(route + "/", StringComparison.OrdinalIgnoreCase);
        });

        return match is null ? [segment] : [match.Key];
    }

    /// <summary>
    /// 菜单 item key。
    /// <para>与 <c>AppSidebar.razor</c> 的 <c>NavKey</c> 等价（<c>Route.Trim('/').Replace('/','-')</c>）。</para>
    /// </summary>
    public static string NavKey(string route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return route.Trim('/').Replace('/', '-');
    }

    private static int EffectiveGroupOrder(IGrouping<string, NavView> group)
    {
        // GroupOrder 未声明（0）视为「排在所有已声明之后」——
        // 否则第三方分组会因默认 0 排到「控制中心」之前（审查 D12）。
        var declared = group.Where(v => v.GroupOrder > 0).Select(v => v.GroupOrder).ToList();
        return declared.Count == 0 ? GroupOrders.Unspecified : declared.Min();
    }

    /// <summary>更短前缀隐藏子路径（对齐迁移前的 <c>FilterSidebarNav</c> 规则）。</summary>
    private static bool HasShorterPrefix(string route, IReadOnlyList<string> candidates)
    {
        var normalized = UiRouteTemplate.NormalizePath(route);

        foreach (var other in candidates)
        {
            var prefix = UiRouteTemplate.NormalizePath(other);
            if (prefix.Length >= normalized.Length)
                continue;

            if (normalized.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

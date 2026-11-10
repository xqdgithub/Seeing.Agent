using Seeing.Agent.Abstractions.Ui;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// <b>过渡期</b>旧贡献注册表：只服务「设置页卡片」与「会话插槽」两条尚未迁移的通道。
/// <para>
/// 页面路由与侧栏已在批次 C 切到 <see cref="Seeing.Agent.Core.Ui.UiContributionRegistry"/>
/// （契约 <see cref="IUiContributionCatalog"/>）：本类的 <c>NavItems</c> / <c>Routes</c> 已无消费方，
/// 仅为满足旧接口而保留。
/// </para>
/// <para>
/// <b>批次 D 会删除本类与旧接口</b>（设置卡与插槽届时改用 <c>SettingsCardContribution</c> /
/// <c>SlotContribution</c> 信封 + <c>UiVisibility</c> + 视图表）。在那之前它仍是 4 张设置卡与
/// 文件面板插槽的唯一数据源，删掉会让设置页只剩两个静态页签、会话右侧面板消失。
/// </para>
/// </summary>
public sealed class LegacyUiContributionRegistry : IUiContributionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IUiContribution> _byModuleId =
        new(StringComparer.OrdinalIgnoreCase);

    private List<NavContribution> _navItems = [];
    private List<LegacySettingsCardContribution> _settingsCards = [];
    private List<LegacySlotContribution> _slots = [];
    private Dictionary<string, NavContribution> _routes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public IReadOnlyList<NavContribution> NavItems
    {
        get { lock (_gate) return _navItems; }
    }

    /// <inheritdoc />
    public IReadOnlyList<LegacySettingsCardContribution> SettingsCards
    {
        get { lock (_gate) return _settingsCards; }
    }

    /// <inheritdoc />
    public IReadOnlyList<LegacySlotContribution> Slots
    {
        get { lock (_gate) return _slots; }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, NavContribution> Routes
    {
        get { lock (_gate) return _routes; }
    }

    /// <summary>贡献集合变更时触发（供 Settings / SlotHost 刷新）。</summary>
    public event EventHandler? Changed;

    /// <inheritdoc />
    public void Register(IUiContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        ArgumentException.ThrowIfNullOrWhiteSpace(contribution.ModuleId);

        lock (_gate)
        {
            _byModuleId[contribution.ModuleId] = contribution;
            RebuildUnlocked();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Unregister(string moduleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);

        var removed = false;
        lock (_gate)
        {
            removed = _byModuleId.Remove(moduleId);
            if (removed)
                RebuildUnlocked();
        }

        if (removed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildUnlocked()
    {
        var nav = new List<NavContribution>();
        var settings = new List<LegacySettingsCardContribution>();
        var slots = new List<LegacySlotContribution>();
        var routes = new Dictionary<string, NavContribution>(StringComparer.OrdinalIgnoreCase);

        foreach (var contribution in _byModuleId.Values.OrderBy(c => c.ModuleId, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var item in contribution.Contribute())
            {
                switch (item)
                {
                    case NavContribution navItem:
                        nav.Add(navItem);
                        if (!string.IsNullOrWhiteSpace(navItem.Route))
                            routes[navItem.Route] = navItem;
                        break;
                    case RouteContribution routeItem:
                        if (!string.IsNullOrWhiteSpace(routeItem.Route))
                        {
                            routes[routeItem.Route] = new NavContribution(
                                routeItem.Route,
                                routeItem.Title,
                                Icon: "",
                                Requires: routeItem.Requires ?? [],
                                ComponentType: routeItem.ComponentType);
                        }
                        break;
                    case LegacySettingsCardContribution settingsItem:
                        settings.Add(settingsItem);
                        break;
                    case LegacySlotContribution slotItem:
                        slots.Add(slotItem);
                        break;
                }
            }
        }

        _navItems = nav;
        _settingsCards = settings;
        _slots = slots;
        _routes = routes;
    }
}

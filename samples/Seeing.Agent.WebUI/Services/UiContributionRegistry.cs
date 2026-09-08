using Seeing.Agent.Abstractions.Ui;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// UI 贡献注册表 — 合并各模块 <see cref="IUiContribution"/>，供侧栏/设置/路由消费。
/// </summary>
public sealed class UiContributionRegistry : IUiContributionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IUiContribution> _byModuleId =
        new(StringComparer.OrdinalIgnoreCase);

    private List<NavContribution> _navItems = [];
    private List<SettingsCardContribution> _settingsCards = [];
    private List<SlotContribution> _slots = [];
    private List<MessageRendererContribution> _messageRenderers = [];
    private Dictionary<string, NavContribution> _routes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public IReadOnlyList<NavContribution> NavItems
    {
        get { lock (_gate) return _navItems; }
    }

    /// <inheritdoc />
    public IReadOnlyList<SettingsCardContribution> SettingsCards
    {
        get { lock (_gate) return _settingsCards; }
    }

    /// <inheritdoc />
    public IReadOnlyList<SlotContribution> Slots
    {
        get { lock (_gate) return _slots; }
    }

    /// <inheritdoc />
    public IReadOnlyList<MessageRendererContribution> MessageRenderers
    {
        get { lock (_gate) return _messageRenderers; }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, NavContribution> Routes
    {
        get { lock (_gate) return _routes; }
    }

    /// <summary>路由表或贡献集合变更时触发（供 ModuleRouter / 侧栏刷新）。</summary>
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
        var settings = new List<SettingsCardContribution>();
        var slots = new List<SlotContribution>();
        var renderers = new List<MessageRendererContribution>();
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
                            // Routes 值类型仍为 NavContribution，供 ModuleRouteResolver 消费；不进侧栏。
                            routes[routeItem.Route] = new NavContribution(
                                routeItem.Route,
                                routeItem.Title,
                                Icon: "",
                                Requires: routeItem.Requires ?? [],
                                ComponentType: routeItem.ComponentType);
                        }
                        break;
                    case SettingsCardContribution settingsItem:
                        settings.Add(settingsItem);
                        break;
                    case SlotContribution slotItem:
                        slots.Add(slotItem);
                        break;
                    case MessageRendererContribution rendererItem:
                        renderers.Add(rendererItem);
                        break;
                }
            }
        }

        _navItems = nav;
        _settingsCards = settings;
        _slots = slots;
        _messageRenderers = renderers;
        _routes = routes;
    }
}

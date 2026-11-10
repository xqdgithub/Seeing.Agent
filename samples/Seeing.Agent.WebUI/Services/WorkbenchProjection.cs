using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Abstractions.Ui.Workbench;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Scenarios;
using Seeing.Agent.Core.Ui;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 会话工作台可见集投影（Scoped，circuit 维度）。
/// <para>
/// <b>为什么抽成一个服务</b>：会话说「有哪些工作台可开」的地方有两处 ——
/// 停靠宿主（渲染 dock）与工具栏选择器（列出可开项）。两处各写一遍判定，就会出现
/// 「选择器里能点、点了打不开」这类分叉。这里让它们读同一份投影。
/// </para>
/// <para>
/// 判定本身仍是 Core 的 <see cref="UiVisibility"/> 唯一实现；本类只负责组装上下文
/// （模块目录 + 会话工作模式门），不复制任何规则。
/// </para>
/// </summary>
public sealed class WorkbenchProjection : IDisposable
{
    private readonly IUiContributionRegistry _registry;
    private readonly IUiPointCatalog _points;
    private readonly IModuleCatalog? _modules;
    private readonly IScenarioCatalog? _scenarios;
    private readonly IOptionsMonitor<SeeingAgentOptions> _options;

    private readonly object _gate = new();
    private readonly Dictionary<string, IReadOnlyList<WorkbenchContribution>> _cache = new(StringComparer.Ordinal);

    /// <summary>创建投影。</summary>
    public WorkbenchProjection(
        IUiContributionRegistry registry,
        IUiPointCatalog points,
        IOptionsMonitor<SeeingAgentOptions> options,
        IModuleCatalog? modules = null,
        IScenarioCatalog? scenarios = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _points = points ?? throw new ArgumentNullException(nameof(points));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _modules = modules;
        _scenarios = scenarios;

        _registry.Changed += OnRegistryChanged;
        _options.OnChange(_ => Invalidate());
    }

    /// <summary>可见集变化（注册表、模块启停或工作模式变化）。</summary>
    public event Action? Changed;

    /// <summary>
    /// 取某会话可见的工作台（按 <c>Order</c> → <c>Id</c> 稳定排序）。
    /// </summary>
    /// <param name="sessionScenario">会话级工作模式；null = 跟随进程级。</param>
    public IReadOnlyList<WorkbenchContribution> GetVisible(string? sessionScenario)
    {
        var cacheKey = sessionScenario ?? "\u0000process";
        lock (_gate)
        {
            if (_cache.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        var result = Compute(sessionScenario);

        lock (_gate)
            _cache[cacheKey] = result;

        return result;
    }

    private IReadOnlyList<WorkbenchContribution> Compute(string? sessionScenario)
    {
        if (_points.Find(WorkbenchPoints.DockId) is not { } point)
            return [];

        var effective = UiScenarioGate.ResolveEffective(sessionScenario, _options.CurrentValue.Scenario);
        var gate = UiScenarioGate.Resolve(effective, _scenarios);
        var context = new UiVisibilityContext(_modules, gate);

        return _registry.Get<WorkbenchContribution>(WorkbenchPoints.DockId)
            .Where(contribution => UiVisibility.IsVisible(contribution, point, context))
            .OrderBy(contribution => contribution.Order)
            .ThenBy(contribution => contribution.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private void OnRegistryChanged(object? sender, EventArgs e) => Invalidate();

    private void Invalidate()
    {
        lock (_gate)
            _cache.Clear();

        Changed?.Invoke();
    }

    /// <inheritdoc />
    public void Dispose() => _registry.Changed -= OnRegistryChanged;
}

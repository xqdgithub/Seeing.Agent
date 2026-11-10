using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Core.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Seeing.Agent.Core.Ui;

/// <summary>
/// <see cref="IUiContributionQuery"/> 的唯一实现：把「不可见」的判定委托给 <see cref="UiVisibility"/>。
/// <para>
/// <b>不复制判定逻辑</b>：这里只做「取候选集 → 逐条问 <see cref="UiVisibility.IsVisible"/> → 过滤」，
/// 模块启用与工作模式收窄仍由 Core 那唯一一份实现决定。任何把它重写成
/// <c>Catalog.IsEnabled(...)</c> 的改动都会制造第二份实现，正是信封重构要消除的反模式。
/// </para>
/// <para>无扩展点声明（无界面宿主）时返回空集。</para>
/// </summary>
public sealed class UiContributionQuery : IUiContributionQuery
{
    private readonly IUiContributionRegistry _registry;
    private readonly IUiPointCatalog? _points;
    private readonly IModuleCatalog? _modules;
    private readonly IScenarioCatalog? _scenarios;
    private readonly ILogger _logger;

    /// <summary>创建查询。</summary>
    /// <param name="registry">贡献注册表（事实来源）。</param>
    /// <param name="points">扩展点目录；null 表示无界面宿主。</param>
    /// <param name="modules">模块目录（启用判定）；缺失时 fail-closed。</param>
    /// <param name="scenarios">工作模式目录（会话门）；缺失时回退内置。</param>
    /// <param name="logger">可选日志。</param>
    public UiContributionQuery(
        IUiContributionRegistry registry,
        IUiPointCatalog? points = null,
        IModuleCatalog? modules = null,
        IScenarioCatalog? scenarios = null,
        ILogger? logger = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _points = points;
        _modules = modules;
        _scenarios = scenarios;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public IReadOnlyList<T> GetVisible<T>(string pointId, string? sessionScenario = null)
        where T : UiContribution
    {
        if (string.IsNullOrWhiteSpace(pointId) || _points?.Find(pointId) is not { } point)
            return [];

        var gate = point.Scope == UiVisibilityScope.Session
            ? UiScenarioGate.Resolve(sessionScenario, _scenarios)
            : null;

        var context = new UiVisibilityContext(_modules, gate);

        return _registry.Get<T>(pointId)
            .Where(contribution => UiVisibility.IsVisible(contribution, point, context))
            .ToArray();
    }
}

/// <summary>UI 查询端口的 DI 注册辅助。</summary>
public static class UiContributionQueryExtensions
{
    /// <summary>注册 <see cref="IUiContributionQuery"/>（幂等）。</summary>
    /// <remarks>
    /// 由 <c>AddSeeingCore</c> 调用。放在这里而不是 <c>UiContributionRegistrationExtensions</c>：
    /// 那个类的职责是「宿主声明扩展点」，本方法是 Core 内部适配器的接线。
    /// </remarks>
    public static IServiceCollection AddSeeingUiContributionQuery(this IServiceCollection services)
    {
        services.TryAddSingleton<IUiContributionQuery>(sp => new UiContributionQuery(
            sp.GetRequiredService<IUiContributionRegistry>(),
            sp.GetService<IUiPointCatalog>(),
            sp.GetService<IModuleCatalog>(),
            sp.GetService<IScenarioCatalog>(),
            sp.GetService<ILogger<UiContributionQuery>>()));

        return services;
    }
}

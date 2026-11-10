using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Ui;

namespace Seeing.Agent.Core.Ui;

/// <summary>可见性判定输入。</summary>
/// <param name="Catalog">模块目录；null 时 fail-closed。</param>
/// <param name="ScenarioModuleGate">
/// 会话工作模式的模块集。<b>null = 不收敛</b>（未指定工作模式或声明了通配）；
/// <b>空集 = 全部隐藏</b>（工作模式声明了「一个模块都不启用」）。
/// <para>两者必须区分：旧实现里空集走 <c>Contains</c> 全 false → 隐藏，属 fail-closed。</para>
/// </param>
public sealed record UiVisibilityContext(
    IModuleCatalog? Catalog,
    IReadOnlySet<string>? ScenarioModuleGate = null)
{
    public static UiVisibilityContext Process(IModuleCatalog? catalog) => new(catalog);
}

public sealed record UiVisibilityDecision(bool Visible, UiVisibilityReason Reason, string? Detail = null);

public enum UiVisibilityReason
{
    Visible,
    CatalogUnavailable,
    ModuleDisabled,
    InvalidRequire,
    ScenarioMismatch
}

/// <summary>可见性唯一实现。</summary>
public static class UiVisibility
{
    public static bool IsVisible(UiContribution contribution, UiPoint point, UiVisibilityContext context)
        => Evaluate(contribution, point, context).Visible;

    public static UiVisibilityDecision Evaluate(
        UiContribution contribution, UiPoint point, UiVisibilityContext context)
    {
        if (contribution.Requires.Count == 0)
        {
            // 进程级贡献（壳层 / 宿主）不受模块目录影响。
            // 与现状 UiContributionVisibility.cs:85-87 一致：空 Requires 先返回 true，再看 catalog。
            // 这一点是必需的：catalog 缺失时若壳层也隐藏，用户会失去 /settings 而无法自救。
            return new UiVisibilityDecision(true, UiVisibilityReason.Visible);
        }

        if (context.Catalog is null)
            return new UiVisibilityDecision(false, UiVisibilityReason.CatalogUnavailable);

        foreach (var raw in contribution.Requires)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new UiVisibilityDecision(false, UiVisibilityReason.InvalidRequire, "Requires 含空白项");

            var id = raw.Trim();
            if (!context.Catalog.IsEnabled(id))
                return new UiVisibilityDecision(false, UiVisibilityReason.ModuleDisabled, id);
        }

        // 会话门收窄：只对 Session 作用域生效。
        // null = 不收敛；空集 = 全部隐藏（与旧实现 SessionShellVisibility.cs:84-95 的 Contains 全 false 等价）
        if (point.Scope == UiVisibilityScope.Session
            && context.ScenarioModuleGate is { } gate
            && !IsInGate(contribution, gate))
        {
            return new UiVisibilityDecision(
                false, UiVisibilityReason.ScenarioMismatch, string.Join(",", contribution.Requires));
        }

        return new UiVisibilityDecision(true, UiVisibilityReason.Visible);
    }

    /// <summary>
    /// 归属是否落在会话门集内。
    /// <para>
    /// <b>AND 语义</b>：<c>Requires</c> 的每一项都必须在门集内（与旧实现
    /// <c>SessionShellVisibility.cs:84-95</c> 的 <c>foreach { if (!contains) return false; }</c> 一致）。
    /// </para>
    /// <para>空 <c>Requires</c>（进程级贡献）不受门集收窄——壳层与宿主贡献必须始终可用。</para>
    /// </summary>
    private static bool IsInGate(UiContribution contribution, IReadOnlySet<string> gate)
    {
        if (contribution.Requires.Count == 0)
            return true;

        foreach (var raw in contribution.Requires)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            if (!gate.Contains(raw.Trim()))
                return false;
        }

        return true;
    }
}

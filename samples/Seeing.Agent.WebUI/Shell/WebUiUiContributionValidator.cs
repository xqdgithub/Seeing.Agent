using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Abstractions.Ui.Workbench;

namespace Seeing.Agent.WebUI.Shell;

/// <summary>
/// 宿主侧贡献校验：页面、设置卡、插槽与工作台的 <c>ViewKey</c> 必须能在视图表里解析。
/// <para>
/// <b>为什么在收集期拦下</b>：视图缺失若拖到渲染期才发现，用户会看到「空白页」而没有任何线索；
/// 拒收后条目进 <c>Diagnostics</c>，启动日志一次性列全，修的是声明而不是渲染分支。
/// 这也是规格 §5.3「视图缺失降级表」的兜底：有校验器时运行期走不到 null。
/// </para>
/// <para>
/// 插槽（<see cref="SlotContribution"/>）与工作台（<see cref="WorkbenchContribution"/>）
/// 同样校验：两者都是 <c>ViewKey</c> 驱动的。
/// </para>
/// </summary>
public sealed class WebUiUiContributionValidator : IUiContributionValidator
{
    private readonly IUiViewKeyCatalog _views;

    /// <summary>创建校验器。</summary>
    public WebUiUiContributionValidator(IUiViewKeyCatalog views)
        => _views = views ?? throw new ArgumentNullException(nameof(views));

    /// <inheritdoc />
    public string? Validate(UiContribution contribution, UiPoint point)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        ArgumentNullException.ThrowIfNull(point);

        var viewKey = contribution switch
        {
            PageContribution page => page.ViewKey,
            SettingsCardContribution card => card.ViewKey,
            SlotContribution slot => slot.ViewKey,
            WorkbenchContribution workbench => workbench.ViewKey,
            _ => null,
        };

        if (viewKey is null)
            return null;

        if (string.IsNullOrWhiteSpace(viewKey))
            return $"贡献 '{contribution.Id}' 的 ViewKey 为空";

        return _views.CanResolve(viewKey)
            ? null
            : $"视图键 '{viewKey}' 未在宿主视图表登记（贡献 '{contribution.Id}'，扩展点 '{point.Id}'）";
    }
}

namespace Seeing.Agent.Abstractions.Ui;

/// <summary>
/// UI 贡献注册表。WebUI 从注册表渲染侧栏、设置页、插槽与消息渲染器。
/// </summary>
public interface IUiContributionRegistry
{
    /// <summary>登记模块 UI 贡献。</summary>
    void Register(IUiContribution contribution);

    /// <summary>全部导航项。</summary>
    IReadOnlyList<NavContribution> NavItems { get; }

    /// <summary>全部设置页卡片。</summary>
    IReadOnlyList<SettingsCardContribution> SettingsCards { get; }

    /// <summary>全部插槽贡献。</summary>
    IReadOnlyList<SlotContribution> Slots { get; }

    /// <summary>全部消息渲染器贡献。</summary>
    IReadOnlyList<MessageRendererContribution> MessageRenderers { get; }

    /// <summary>路由表（路由 → 导航贡献）。</summary>
    IReadOnlyDictionary<string, NavContribution> Routes { get; }
}

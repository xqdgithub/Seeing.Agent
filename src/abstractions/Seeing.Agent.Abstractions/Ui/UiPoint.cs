namespace Seeing.Agent.Abstractions.Ui;

/// <summary>载荷种类（封闭集）。</summary>
public enum UiPayloadKind
{
    Page,
    SettingsCard,
    Slot,

    /// <summary>会话工作台（可停靠的持久视图，见 <c>WorkbenchContribution</c>）。</summary>
    Workbench
}

/// <summary>扩展点可见性作用域。</summary>
public enum UiVisibilityScope
{
    /// <summary>进程级：只看模块启用集（侧栏 / 设置卡 / 页面）。</summary>
    Process,

    /// <summary>会话级：额外随会话工作模式收窄（会话插槽）。</summary>
    Session
}

/// <summary>UI 扩展点：宿主声明「这里能挂什么」。</summary>
public sealed record UiPoint
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required UiPayloadKind PayloadKind { get; init; }
    public UiVisibilityScope Scope { get; init; } = UiVisibilityScope.Process;
    public int MaxItems { get; init; } = int.MaxValue;
}

/// <summary>宿主支持的扩展点集合。</summary>
public interface IUiPointCatalog
{
    IReadOnlyList<UiPoint> Points { get; }
    UiPoint? Find(string pointId);
    bool Supports(string pointId);
}

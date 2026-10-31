namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 跨组件 UI 覆盖层请求（Scoped = circuit 维度）。
/// <para>
/// 「审批中心」是抽屉而非路由，主页卡片无法用导航打开它；由本服务发出请求，
/// 承载抽屉的 <c>AppSidebar</c> 订阅后打开。后续若还有「从任意页面唤出某个覆盖层」，
/// 继续在这里加请求方法即可。
/// </para>
/// </summary>
public sealed class UiOverlayService
{
    /// <summary>请求打开审批中心抽屉。</summary>
    public event Action? PermissionCenterRequested;

    /// <summary>发出「打开审批中心」请求；无订阅者时静默忽略。</summary>
    public void RequestPermissionCenter() => PermissionCenterRequested?.Invoke();
}

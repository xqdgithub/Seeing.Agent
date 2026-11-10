namespace Seeing.Agent.WebUI.Shell;

/// <summary>
/// 插槽视图键常量。
/// <para>
/// 页面与设置卡的 ViewKey 直接与贡献 <c>Id</c> 同名，无需常量；插槽只有一条内置贡献
/// （文件面板），但它的声明在 <c>Services/FilePanelUiContribution</c>、登记在
/// <c>WebUiViews</c>，两处必须逐字符一致，因此抽成常量。
/// </para>
/// </summary>
public static class WebUiViewKeys
{
    /// <summary>会话右侧文件面板（<c>session.file.panel</c> 插槽）。</summary>
    public const string SessionFilePanel = "slot.file.panel";
}

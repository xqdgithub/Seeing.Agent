using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Abstractions.Ui.Workbench;

namespace Seeing.Agent.WebUI.Shell;

/// <summary>
/// WebUI 宿主声明的 UI 扩展点（「这里能挂什么」）。
/// <para>
/// <b>必须在 <c>Build()</c> 之前经 <c>AddSeeingUiPoints(WebUiPoints.All)</c> 登记</b>：
/// 贡献在结算后收集，若此时目录为空，所有贡献都会被判为「未声明的扩展点」而拒收 → UI 全空。
/// </para>
/// <para>
/// <see cref="UiPoint.Id"/> 只增不改（无版本机制，进程内编译期组装，错配在编译期爆）。
/// </para>
/// </summary>
public static class WebUiPoints
{
    /// <summary>页面（侧栏 Nav 与纯路由共用同一扩展点；<c>Nav == null</c> 表示不进侧栏）。</summary>
    public static readonly UiPoint ShellPage = new()
    {
        Id = "shell.page",
        DisplayName = "页面",
        PayloadKind = UiPayloadKind.Page,
    };

    /// <summary>设置页卡片。</summary>
    public static readonly UiPoint ShellSettingsCard = new()
    {
        Id = "shell.settings.card",
        DisplayName = "设置卡片",
        PayloadKind = UiPayloadKind.SettingsCard,
    };

    /// <summary>会话输入区上方（<c>session_prompt</c>）。</summary>
    public static readonly UiPoint SessionPrompt = new()
    {
        Id = "session.prompt",
        DisplayName = "输入区上方",
        PayloadKind = UiPayloadKind.Slot,
        Scope = UiVisibilityScope.Session,
    };

    /// <summary>会话工具栏右侧（<c>session_toolbar_actions</c>）。</summary>
    public static readonly UiPoint SessionToolbarActions = new()
    {
        Id = "session.toolbar.actions",
        DisplayName = "会话工具栏右侧",
        PayloadKind = UiPayloadKind.Slot,
        Scope = UiVisibilityScope.Session,
    };

    /// <summary>输入框工具栏（<c>message_toolbar</c>）。</summary>
    public static readonly UiPoint SessionInputToolbar = new()
    {
        Id = "session.input.toolbar",
        DisplayName = "输入框工具栏",
        PayloadKind = UiPayloadKind.Slot,
        Scope = UiVisibilityScope.Session,
    };

    /// <summary>
    /// 会话工作台停靠位（<c>workbench.dock</c>）。
    /// <para>
    /// <b>会话级</b>：工作台随会话工作模式收窄（与插槽同语义）。
    /// 它取代了迁移前的 <c>session.file.panel</c> 插槽 —— 插槽只能「挂一个固定组件」，
    /// 工作台则是「可并存、可切换、可持久化、可被 agent 驱动的视图」。
    /// </para>
    /// </summary>
    public static readonly UiPoint WorkbenchDock = new()
    {
        Id = WorkbenchPoints.DockId,
        DisplayName = "会话工作台",
        PayloadKind = UiPayloadKind.Workbench,
        Scope = UiVisibilityScope.Session,
    };

    /// <summary>全部扩展点。</summary>
    public static IReadOnlyList<UiPoint> All { get; } =
    [
        ShellPage,
        ShellSettingsCard,
        SessionPrompt,
        SessionToolbarActions,
        SessionInputToolbar,
        WorkbenchDock,
    ];
}

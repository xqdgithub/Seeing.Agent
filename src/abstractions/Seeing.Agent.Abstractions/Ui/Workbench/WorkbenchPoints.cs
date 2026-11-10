namespace Seeing.Agent.Abstractions.Ui.Workbench;

/// <summary>
/// 工作台扩展点的 id 与默认约束（宿主与能力层共享的字符串协议）。
/// <para>
/// <b>为什么常量住在契约层</b>：宿主声明这个点、能力层按这个点查目录、工作台按这个点声明贡献。
/// 三处字符串一旦漂移，症状是「agent 列不出工作台」这种极难定位的静默失败。
/// </para>
/// </summary>
public static class WorkbenchPoints
{
    /// <summary>工作台停靠扩展点 id（<see cref="UiVisibilityScope.Session"/>）。</summary>
    public const string DockId = "workbench.dock";

    /// <summary>工作台视图键前缀（宿主视图表按此约定登记，便于守门测试区分工作台视图）。</summary>
    public const string ViewKeyPrefix = "workbench.";

    /// <summary>按工作台 id 推导默认视图键（约定：<c>workbench.{id}</c>）。</summary>
    public static string ViewKeyFor(string workbenchId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbenchId);
        return ViewKeyPrefix + workbenchId;
    }
}

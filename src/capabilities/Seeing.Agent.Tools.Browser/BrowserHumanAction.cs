namespace Seeing.Agent.Tools.Browser;

/// <summary>
/// 人工操作的一条记录（人在工作台画布上直接操作浏览器时产生）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要记录</b>：agent 与人在同一个页面上操作时，agent 看不到用户做了什么。
/// 用户自己点了「下一页」、填了表单、滚到某处，agent 后续的 ref 与判断就全部失去依据——
/// 轻则答非所问，重则点错元素。记录这些动作至少让两个问题可解：
/// </para>
/// <list type="bullet">
/// <item><b>审计</b>：事后能查「agent 干活期间人到底动了什么」（写结构化日志）。</item>
/// <item><b>告知</b>：接管发生时（或下一次用户发言前），可把这些动作摘要给 agent，
/// 让它知道自己看到的页面为什么变了。</item>
/// </list>
/// <para>
/// <b>为什么不做可回放</b>：坐标点击在页面结构变化后必然失效，ref 只在同一
/// <c>RefEpoch</c> 内有效。回放工具会给出「看起来能用、实际常常打偏」的假能力，
/// 不如不做（用户确认：这个功能不常用，以后再补）。
/// </para>
/// </remarks>
public sealed record BrowserHumanAction
{
    /// <summary>发生时刻（UTC）。</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>
    /// 动作种类：<c>click</c> / <c>type</c> / <c>key</c> / <c>scroll</c> / <c>navigate</c>。
    /// </summary>
    /// <remarks>
    /// 用开放字符串而不是枚举：日志与告知文本都要直接读它，
    /// 后续加新种类（如 <c>drag</c>）不必改动消费方。
    /// </remarks>
    public required string Kind { get; init; }

    /// <summary>操作目标（ref、坐标、按键名，或滚动的方向/距离）。</summary>
    public required string Target { get; init; }

    /// <summary>输入内容（仅 <c>type</c>；敏感字段已脱敏为 <c>***</c>）。</summary>
    public string? Value { get; init; }

    /// <summary>操作发生时的页面地址。</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>渲染成一行人类可读文本（用于告知 agent 与日志）。</summary>
    public string Render()
    {
        var value = string.IsNullOrEmpty(Value) ? string.Empty : $" “{Value}”";
        return Kind switch
        {
            "type" => $"在 {Target} 输入{value}",
            "click" => $"点击 {Target}",
            "key" => $"按键 {Target}",
            "scroll" => $"滚动 {Target}",
            "navigate" => $"导航到 {Target}",
            _ => $"{Kind} {Target}"
        };
    }
}

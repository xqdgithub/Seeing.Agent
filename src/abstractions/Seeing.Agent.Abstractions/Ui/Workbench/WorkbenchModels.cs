namespace Seeing.Agent.Abstractions.Ui.Workbench;

/// <summary>工作台停靠位。</summary>
/// <remarks>
/// 左右各一个独立 dock，各自持有自己的实例集合、激活项与宽度。
/// 同一工作台可以同时在两侧各开一个实例。
/// </remarks>
public enum WorkbenchDock
{
    /// <summary>左侧（默认不出现，只有用户/agent 显式指定才用）。</summary>
    Left,

    /// <summary>右侧（默认）。</summary>
    Right
}

/// <summary>dock 内的实例键。</summary>
/// <param name="Dock">停靠位。</param>
/// <param name="WorkbenchId">工作台 id（即 <see cref="WorkbenchContribution.Id"/>）。</param>
/// <param name="Ordinal">同 dock 同工作台的序号，从 0 开始（本期每 dock 每工作台一个实例）。</param>
public readonly record struct WorkbenchInstanceKey(
    WorkbenchDock Dock,
    string WorkbenchId,
    int Ordinal = 0)
{
    /// <summary>稳定文本形式（用于 localStorage 与诊断日志）。</summary>
    public override string ToString() => $"{Dock}:{WorkbenchId}#{Ordinal}";

    /// <summary>解析 <see cref="ToString"/> 的输出；格式非法返回 false。</summary>
    public static bool TryParse(string? text, out WorkbenchInstanceKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split(':');
        if (parts.Length != 2)
            return false;

        if (!Enum.TryParse<WorkbenchDock>(parts[0], ignoreCase: true, out var dock))
            return false;

        var rest = parts[1];
        var hash = rest.LastIndexOf('#');
        var workbenchId = hash >= 0 ? rest[..hash] : rest;
        if (string.IsNullOrWhiteSpace(workbenchId))
            return false;

        var ordinal = 0;
        if (hash >= 0 && !int.TryParse(rest[(hash + 1)..], out ordinal))
            return false;

        key = new WorkbenchInstanceKey(dock, workbenchId, ordinal);
        return true;
    }
}

/// <summary>尺寸偏好：百分比相对会话内容区宽度，<see cref="MinPx"/> 为硬下限。</summary>
/// <param name="DefaultPercent">
/// 无持久化宽度时的初值。50% 与迁移前的文件面板一致 —— 只开一个工作台时它就该占半个屏。
/// 两侧同时开时由 <c>WorkbenchDockState.MaxCombinedPercent</c> 的预算等比压缩，不靠调小默认值来避让。
/// </param>
/// <param name="MinPercent">可拖到的最小百分比。</param>
/// <param name="MaxPercent">可拖到的最大百分比。</param>
/// <param name="MinPx">即使百分比算出来更小，也不低于该像素宽。</param>
public sealed record WorkbenchSizing(
    double DefaultPercent = WorkbenchSizing.DefaultPercentValue,
    double MinPercent = 20,
    double MaxPercent = 75,
    double MinPx = 320)
{
    /// <summary>默认宽度百分比的编译期常量（供其他默认实参引用）。</summary>
    public const double DefaultPercentValue = 50;

    /// <summary>默认尺寸（未声明 <see cref="WorkbenchContribution.Sizing"/> 时的唯一缺省值）。</summary>
    public static WorkbenchSizing Default { get; } = new();
}

/// <summary>UI 侧上报的工作台视图报告。</summary>
/// <remarks>
/// UI 是工作台内部状态的唯一持有者；服务端只消费这份<b>已确认的稳定快照</b>，
/// 不猜测工作台内部结构（框架不知道视频播放到第几秒，也不该知道）。
/// </remarks>
public sealed record WorkbenchViewReport
{
    /// <summary>可恢复小状态（序列化后 ≤2KB）；null 表示不覆盖已存值。</summary>
    public IReadOnlyDictionary<string, string>? ViewState { get; init; }

    /// <summary>当前查看内容的单行摘要（仅 <see cref="WorkbenchContribution.ContributesContext"/> 为 true 时需要，≤200 字）。</summary>
    public string? ContextSummary { get; init; }
}

/// <summary>服务端可见的会话工作台快照（由 UI 推送，服务端只读）。</summary>
/// <param name="Key">实例键。</param>
/// <param name="Title">工作台标题（来自贡献声明）。</param>
/// <param name="Active">是否是所在 dock 的激活项。</param>
/// <param name="ContextSummary">当前查看内容的单行摘要；不贡献上下文的工作台恒为 null。</param>
public sealed record WorkbenchSessionSnapshot(
    WorkbenchInstanceKey Key,
    string Title,
    bool Active,
    string? ContextSummary);

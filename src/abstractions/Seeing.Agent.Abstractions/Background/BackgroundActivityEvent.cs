using Seeing.Agent.Abstractions.Events;

namespace Seeing.Agent.Abstractions.Background;

/// <summary>后台活动事件类型常量（点分开放字符串）。</summary>
public static class ActivityEventTypes
{
    /// <summary>活动进入 <see cref="BackgroundActivityStatus.Running"/>。</summary>
    public const string Started = "activity.started";

    /// <summary>活动进度（必须节流，≥1s）。</summary>
    public const string Progress = "activity.progress";

    /// <summary>活动成功完成。</summary>
    public const string Completed = "activity.completed";

    /// <summary>活动失败。</summary>
    public const string Failed = "activity.failed";

    /// <summary>活动被取消。</summary>
    public const string Cancelled = "activity.cancelled";
}

/// <summary>
/// 后台活动事件——实时 UI 增量载荷（非权威）。
/// <para>
/// 不做事件落盘：UI 首屏用 <see cref="IBackgroundActivityManager.List"/> 重建，本事件只做增量；
/// 不唤醒执行（<c>Inject</c> 语义）。
/// </para>
/// </summary>
public record BackgroundActivityEvent : IMessageEvent
{
    /// <summary>会话 id（归属会话）。</summary>
    public required string SessionId { get; init; }

    /// <summary>Agent Loop id（可能为空：活动可在轮次结束后继续存活）。</summary>
    public string? LoopId { get; init; }

    /// <summary>时间戳。</summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>事件类型，取 <see cref="ActivityEventTypes"/> 之一。</summary>
    public required string Type { get; init; }

    /// <summary>事件发生时的活动快照。</summary>
    public required BackgroundActivitySnapshot Activity { get; init; }

    /// <summary>
    /// 输出尾部（仅 <c>activity.progress</c> 携带）。
    /// <para>
    /// <b>仅供 UI 实时展示</b>：有界（最后 4096 字符），<b>不落盘</b>、不进快照、不参与任何框架判定。
    /// 读者不得把它当作权威输出；权威内容只能经 <c>ReadOutputAsync</c> 读取输出文件。
    /// </para>
    /// </summary>
    public string? OutputTail { get; init; }
}

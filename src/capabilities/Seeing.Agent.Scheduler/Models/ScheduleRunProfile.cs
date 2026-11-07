using Seeing.Agent.Abstractions.Reminders;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Session.Core;

namespace Seeing.Agent.Scheduler.Models;

/// <summary>
/// 一次调度执行的身份画像。
/// <para>
/// 随 JobData 下发，由 <c>AgentJob</c> 读取。缺画像时等于 <see cref="Cron"/>，
/// 用户任务的既有行为（Hook 点、结果来源、提醒 kind、写回布局）完全不变。
/// </para>
/// <para>
/// 心跳与定时任务共用同一个 Job 类型与同一套执行设施，身份差异全部集中在这里：
/// 新增一类调度来源只需加一份画像，不需要再复制一个 Job。
/// </para>
/// </summary>
public sealed class ScheduleRunProfile
{
    /// <summary>执行来源（cron / heartbeat）。写入结果、Hook 载荷与出站来源。</summary>
    public string Source { get; set; } = ScheduleSources.Cron;

    /// <summary>提醒信封 kind。</summary>
    public string ReminderKind { get; set; } = SystemReminder.Kinds.Cron;

    /// <summary>会话消息的系统发送者标签。</summary>
    public string SenderId { get; set; } = "cron";

    /// <summary>仅 conversation 时新建会话的 id 前缀。</summary>
    public string SessionKeyPrefix { get; set; } = "cron";

    /// <summary>仅 conversation 时新建会话的标题前缀。</summary>
    public string SessionTitlePrefix { get; set; } = "⏰";

    /// <summary>新建会话时写入的来源标记。</summary>
    public SessionOrigin OriginWhenCreated { get; set; } = SessionOrigin.Scheduler;

    /// <summary>会话写回布局。</summary>
    public DispatchLayout Layout { get; set; } = DispatchLayout.PromptAndResult;

    /// <summary>定时任务画像（默认）。</summary>
    public static ScheduleRunProfile Cron => new();

    /// <summary>心跳画像：独立来源、提醒 kind、发送者、会话前缀与来源标记，只写助手结果。</summary>
    public static ScheduleRunProfile Heartbeat => new()
    {
        Source = ScheduleSources.Heartbeat,
        ReminderKind = SystemReminder.Kinds.Heartbeat,
        SenderId = "heartbeat",
        SessionKeyPrefix = "hb",
        SessionTitlePrefix = "💓",
        OriginWhenCreated = SessionOrigin.Heartbeat,
        Layout = DispatchLayout.AssistantOnly
    };
}

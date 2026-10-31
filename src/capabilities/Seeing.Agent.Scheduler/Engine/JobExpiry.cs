using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.Scheduler.Engine;

/// <summary>
/// 「已过期」判定：任务仍为启用意图，但之后不会再自动触发。
/// <para>
/// 典型场景：一次性任务的 <c>runAt</c> 已经过去；或限定日期的 cron 已无下一次触发。
/// 这类任务保留在列表中会造成噪音，UI 应单独归栏（默认不展示），但**不改变**任务本身的
/// <see cref="ScheduleIntent"/> —— 它仍然是启用状态，可手动 <c>cron_run</c> 或编辑时间后恢复。
/// </para>
/// </summary>
public static class JobExpiry
{
    /// <summary>判断任务是否已过期（不会再有后续自动触发）。</summary>
    /// <param name="job">任务定义。</param>
    /// <param name="status">调度器返回的运行时状态；为 null 表示尚未取到。</param>
    /// <param name="now">当前时间；内部会换算到任务的时区墙钟后比较。</param>
    /// <param name="schedulerStarted">调度器是否已启动；未启动时非一次性任务无法判断，按未过期处理。</param>
    public static bool IsExpired(
        ScheduledJobSpec job,
        JobStatus? status,
        DateTime now,
        bool schedulerStarted)
    {
        ArgumentNullException.ThrowIfNull(job);

        // 只有启用意图才谈得上“过期”；禁用/暂停已有各自的归类。
        if (job.Intent != ScheduleIntent.Active)
            return false;

        if (string.Equals(job.Schedule.Type, ScheduleTypes.Once, StringComparison.OrdinalIgnoreCase))
        {
            // runAt 是与任务时区无关的墙钟值，比较前把当前时间换算到同一时区。
            var nowWall = ToWallClock(now, job.Schedule.Timezone);

            // runAt 已过去：即使尚未跑到，也不会再等到下一次。
            if (job.Schedule.RunAt is { } runAt && runAt <= nowWall)
                return true;

            // 已触发过一次且无下一次。
            return status is not null && status.NextFireTime is null && status.PreviousFireTime is not null;
        }

        // 其它类型（cron / interval）理论上会持续触发；仅在调度器给出“无下一次”时认定为过期，
        // 且要求已有过触发记录，避免调度器启动瞬间状态未就绪造成误判。
        if (!schedulerStarted || status is null)
            return false;

        return status.NextFireTime is null && status.PreviousFireTime is not null;
    }

    private static DateTime ToWallClock(DateTime now, string? timezoneId)
    {
        var kind = now.Kind == DateTimeKind.Unspecified ? DateTimeKind.Local : now.Kind;
        var utc = kind == DateTimeKind.Utc ? now : now.ToUniversalTime();
        var zone = ScheduleExpressionParser.ResolveTimeZone(timezoneId);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, zone), DateTimeKind.Unspecified);
    }
}

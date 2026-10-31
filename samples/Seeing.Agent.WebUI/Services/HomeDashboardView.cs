using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 主页仪表盘的纯投影逻辑（无副作用，便于测试）。
/// <para>
/// UI 只负责排版与点击；「相对时间怎么分档、空卡要不要显示、哪几个任务算即将触发、过期几个」
/// 都在这里决定，测试覆盖这些边界。
/// </para>
/// </summary>
public static class HomeDashboardView
{
    /// <summary>相对时间：刚刚 / N 分钟前 / N 小时前 / 昨天 / N 天前（未来时间按「刚刚」）。</summary>
    public static string RelativeTime(DateTime time, DateTime now)
    {
        var delta = now - time;
        if (delta < TimeSpan.Zero)
            delta = TimeSpan.Zero;

        if (delta < TimeSpan.FromMinutes(1))
            return "刚刚";
        if (delta < TimeSpan.FromHours(1))
            return $"{(int)delta.TotalMinutes} 分钟前";
        if (delta < TimeSpan.FromHours(24))
            return $"{(int)delta.TotalHours} 小时前";
        if (delta < TimeSpan.FromHours(48))
            return "昨天";

        return $"{(int)delta.TotalDays} 天前";
    }

    /// <summary>待我处理是否该出现：任一项非零才显示整卡（避免空卡噪音）。</summary>
    public static bool HasActionItems(int approvals, int questions, int failures) =>
        approvals > 0 || questions > 0 || failures > 0;

    /// <summary>会话标题兜底：空白 → 「未命名会话」。</summary>
    public static string SessionTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) ? "未命名会话" : title.Trim();

    /// <summary>
    /// 即将触发的任务：仅启用意图、未过期，按时间升序取前 <paramref name="take"/> 个。
    /// 时间优先取调度器的 <see cref="JobStatus.NextFireTime"/>，缺失时按表达式推算。
    /// </summary>
    public static IReadOnlyList<UpcomingRun> UpcomingRuns(
        IReadOnlyList<ScheduledJobSpec> jobs,
        IReadOnlyDictionary<string, JobStatus> statuses,
        DateTime now,
        int take)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(statuses);

        if (take <= 0)
            return Array.Empty<UpcomingRun>();

        var runs = new List<UpcomingRun>();
        foreach (var job in jobs)
        {
            if (job.Intent != ScheduleIntent.Active)
                continue;

            var status = statuses.GetValueOrDefault(job.Id);

            // 首页拿到的是「已启动调度器」的快照；过期判定与 cron_list 保持一致。
            if (JobExpiry.IsExpired(job, status, now, schedulerStarted: true))
                continue;

            var when = status?.NextFireTime;
            if (when is null)
            {
                try
                {
                    when = ScheduleExpressionParser.GetNextOccurrence(job.Schedule, now);
                }
                catch (Exception)
                {
                    // 表达式异常不阻塞整卡渲染：跳过该任务
                    when = null;
                }
            }

            if (when is null)
                continue;

            runs.Add(new UpcomingRun(job.Id, TitleOrId(job), when.Value));
        }

        return runs
            .OrderBy(r => r.When)
            .Take(take)
            .ToList();
    }

    /// <summary>已过期任务数（口径同 <see cref="JobExpiry"/>）。</summary>
    public static int CountExpired(
        IReadOnlyList<ScheduledJobSpec> jobs,
        IReadOnlyDictionary<string, JobStatus> statuses,
        DateTime now,
        bool schedulerStarted)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(statuses);

        return jobs.Count(job =>
            JobExpiry.IsExpired(job, statuses.GetValueOrDefault(job.Id), now, schedulerStarted));
    }

    private static string TitleOrId(ScheduledJobSpec job) =>
        string.IsNullOrWhiteSpace(job.Name) ? job.Id : job.Name.Trim();
}

/// <summary>即将触发的一条任务（主页卡片行）。</summary>
public sealed record UpcomingRun(string JobId, string Title, DateTime When);

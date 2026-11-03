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
        // LastActiveAt 的 Kind 在不同写入路径不一致（Utc / Local 混用），
        // 直接相减会差出一个时区偏移，先统一到 UTC 再比较。
        var delta = ToUtc(now) - ToUtc(time);
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

    /// <summary>Kind 归一：Utc 原样、Local 转 UTC、Unspecified 视为本地墙钟再转 UTC。</summary>
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime()
    };

    /// <summary>待我处理是否该出现：任一项非零才显示整卡（避免空卡噪音）。</summary>
    public static bool HasActionItems(int approvals, int questions, int failures) =>
        approvals > 0 || questions > 0 || failures > 0;

    /// <summary>会话标题兜底：空白 → 「未命名会话」。</summary>
    public static string SessionTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) ? "未命名会话" : title.Trim();

    /// <summary>
    /// 当天（00:00–24:00）的全部触发点，按时间升序。
    /// 已过去的点也保留——时间轴要能看出「今天什么时候有任务」。
    /// </summary>
    /// <param name="perJobLimit">单任务单日最多展开多少个点，防止高频 interval 把小图撑爆。</param>
    public static IReadOnlyList<TodayRun> TodayRuns(
        IReadOnlyList<ScheduledJobSpec> jobs,
        IReadOnlyDictionary<string, JobStatus> statuses,
        DateTime now,
        int perJobLimit = 60)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(statuses);

        if (perJobLimit <= 0)
            return Array.Empty<TodayRun>();

        var dayStart = now.Date;
        var dayEnd = dayStart.AddDays(1);
        var runs = new List<TodayRun>();

        foreach (var job in jobs)
        {
            if (job.Intent != ScheduleIntent.Active)
                continue;

            var status = statuses.GetValueOrDefault(job.Id);

            // 这里只铺「还会触发」的任务。任务本身已过期（以后不会再跑）不该占用时间轴位置：
            // 它在未来没有任何落点，铺出来只会让人误以为今天还有安排。
            if (JobExpiry.IsExpired(job, status, now, schedulerStarted: true))
                continue;

            var cursor = dayStart;
            for (var i = 0; i < perJobLimit; i++)
            {
                DateTime? next;
                try
                {
                    next = ScheduleExpressionParser.GetNextOccurrence(job.Schedule, cursor);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    next = null;
                }

                // 不前进视为收敛（防止表达式异常导致死循环）
                if (next is null || next.Value <= cursor || next.Value >= dayEnd)
                    break;

                runs.Add(new TodayRun(next.Value, TitleOrId(job)));
                cursor = next.Value;
            }
        }

        return runs.OrderBy(r => r.When).ToList();
    }

    /// <summary>
    /// 按小时汇总当天触发点（长度 24，索引 = 小时）。用于「24 小时格子」可视化。
    /// </summary>
    public static IReadOnlyList<int> HourLoads(IReadOnlyList<TodayRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);

        var loads = new int[24];
        foreach (var run in runs)
        {
            var hour = run.When.Hour;
            if (hour is >= 0 and < 24)
                loads[hour]++;
        }

        return loads;
    }

    /// <summary>
    /// 下一条未来触发（跨任务取最早）。今天没有任务时用它给一行「下次：…」提示。
    /// 无可用任务（全部过期/禁用/表达式非法）返回 null。
    /// </summary>
    public static TodayRun? NextRunAfter(
        IReadOnlyList<ScheduledJobSpec> jobs,
        IReadOnlyDictionary<string, JobStatus> statuses,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(statuses);

        TodayRun? best = null;
        foreach (var job in jobs)
        {
            if (job.Intent != ScheduleIntent.Active)
                continue;

            var status = statuses.GetValueOrDefault(job.Id);
            if (JobExpiry.IsExpired(job, status, now, schedulerStarted: true))
                continue;

            DateTime? next;
            try
            {
                next = ScheduleExpressionParser.GetNextOccurrence(job.Schedule, now);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                next = null;
            }

            if (next is null)
                continue;

            if (best is null || next.Value < best.When)
                best = new TodayRun(next.Value, TitleOrId(job));
        }

        return best;
    }

    /// <summary>
    /// 已过期任务数（口径同 <see cref="JobExpiry"/>）。
    /// <para>
    /// <b>注意</b>：这里指的是「任务本身再也不会触发」（一次性任务已过点、cron 无后续），
    /// 与首页「今日已执行」是两个不同概念。首页展示已执行记录，不要用本方法。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// 筛选「今天已经执行过」的记录，按开始时间降序（最近的在前）。
    /// <para>
    /// 口径是<b>执行历史</b>：今天 00:00 之后、且已结束（<see cref="JobExecutionRecord.CompletedAt"/> 非空）
    /// 的执行。进行中的不算「已执行」。
    /// </para>
    /// </summary>
    public static IReadOnlyList<TodayExecution> TodayExecutions(
        IEnumerable<JobExecutionRecord> records,
        DateTime now,
        int limit = 20)
    {
        ArgumentNullException.ThrowIfNull(records);

        var dayStart = now.Date;
        var dayEnd = dayStart.AddDays(1);

        return records
            .Where(r => r.StartedAt >= dayStart && r.StartedAt < dayEnd)
            .Where(r => r.CompletedAt is not null)
            .OrderByDescending(r => r.StartedAt)
            .Take(Math.Max(0, limit))
            .Select(r => new TodayExecution(
                r.StartedAt,
                r.CompletedAt,
                r.JobId,
                r.Status,
                r.TaskType))
            .ToList();
    }

    private static string TitleOrId(ScheduledJobSpec job) =>
        string.IsNullOrWhiteSpace(job.Name) ? job.Id : job.Name.Trim();
}

/// <summary>当天的一个触发点（今日时间轴上的一个标记）。</summary>
public sealed record TodayRun(DateTime When, string Title);

/// <summary>
/// 今天的一条执行记录。
/// <para>
/// 用于「已执行」列表：用户可以确认今天到底跑了什么、成没成功。
/// </para>
/// </summary>
public sealed record TodayExecution(
    DateTime StartedAt,
    DateTime? CompletedAt,
    string JobId,
    string Status,
    string? TaskType)
{
    /// <summary>执行是否成功（<c>success</c>）。</summary>
    public bool IsSuccess =>
        string.Equals(Status, "success", StringComparison.OrdinalIgnoreCase);

    /// <summary>耗时；未结束时为 null。</summary>
    public TimeSpan? Duration => CompletedAt is { } done ? done - StartedAt : null;
}

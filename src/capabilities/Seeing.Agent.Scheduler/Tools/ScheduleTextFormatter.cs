using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Models;
using System.Text;

namespace Seeing.Agent.Scheduler.Tools;

/// <summary>
/// cron 工具的统一文本呈现：调度描述、任务详情块。
/// <para>
/// 工具返回是 Agent/用户理解任务状态的唯一窗口，因此必须包含名称、类型、调度规则、
/// 下次触发时间与提醒内容，而不是只有 id 与状态码。
/// </para>
/// </summary>
internal static class ScheduleTextFormatter
{
    private const int ContentLimit = 160;

    /// <summary>把调度规则转换为单行可读文本。</summary>
    public static string DescribeSchedule(ScheduleSpec? schedule)
    {
        if (schedule is null)
            return "-";

        var timezone = string.IsNullOrWhiteSpace(schedule.Timezone) ? TimeZoneInfo.Local.Id : schedule.Timezone;

        switch (schedule.Type)
        {
            case ScheduleTypes.Cron:
                return string.IsNullOrWhiteSpace(schedule.Cron)
                    ? $"cron（未设置表达式，时区 {timezone}）"
                    : $"cron \"{schedule.Cron}\"（时区 {timezone}）";

            case ScheduleTypes.Interval:
                var windows = DescribeWindows(schedule.Windows);
                return string.IsNullOrEmpty(windows)
                    ? $"每 {schedule.Every}（时区 {timezone}）"
                    : $"每 {schedule.Every}，仅在 {windows}（时区 {timezone}）";

            case ScheduleTypes.Once:
                return $"一次性 {schedule.RunAt:yyyy-MM-dd HH:mm:ss}（时区 {timezone}）";

            default:
                return schedule.Type ?? "-";
        }
    }

    /// <summary>任务类型的中文说明。</summary>
    public static string DescribeTaskType(ScheduledJobSpec job) =>
        string.Equals(job.TaskType, ScheduleTaskTypes.Text, StringComparison.OrdinalIgnoreCase)
            ? "text（固定文案提醒）"
            : "agent（运行 Agent）";

    /// <summary>意图 + 运行时状态（保留枚举原名，便于按 intent 精确检索）。</summary>
    public static string DescribeState(ScheduledJobSpec job, JobStatus? status, DateTime? now = null, bool schedulerStarted = false)
    {
        var intent = job.Intent switch
        {
            ScheduleIntent.Active => "启用",
            ScheduleIntent.Paused => "已暂停",
            ScheduleIntent.Disabled => "已禁用",
            _ => job.Intent.ToString()
        };

        if (JobExpiry.IsExpired(job, status, now ?? DateTime.Now, schedulerStarted))
            return $"{job.Intent}（{intent}，已过期）";

        return status is null
            ? $"{job.Intent}（{intent}）"
            : $"{job.Intent}（{intent}，{status.State}）";
    }

    /// <summary>下次触发时间：优先用调度器真实值，未启动时按表达式推算（仅启用任务）。</summary>
    public static string DescribeNextFire(ScheduledJobSpec job, JobStatus? status, DateTime? now = null, bool schedulerStarted = false)
    {
        if (JobExpiry.IsExpired(job, status, now ?? DateTime.Now, schedulerStarted))
            return "已过期（不会再次自动触发）";

        if (status?.NextFireTime is { } next)
            return next.ToString("yyyy-MM-dd HH:mm:ss");

        if (job.Intent != ScheduleIntent.Active)
            return "-";

        try
        {
            var computed = ScheduleExpressionParser.GetNextOccurrence(job.Schedule, DateTime.Now);
            return computed?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
        }
        catch (Exception)
        {
            return "-";
        }
    }

    /// <summary>提醒正文 / Agent 指令（截断，避免刷屏）。</summary>
    public static string DescribeContent(ScheduledJobSpec job)
    {
        var isText = string.Equals(job.TaskType, ScheduleTaskTypes.Text, StringComparison.OrdinalIgnoreCase);
        var content = isText ? job.Text : job.Prompt;

        if (string.IsNullOrWhiteSpace(content))
            return "-";

        var flat = content.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        return flat.Length <= ContentLimit ? flat : flat[..ContentLimit] + "…";
    }

    /// <summary>单个任务的多行详情块。</summary>
    public static string DescribeJob(ScheduledJobSpec job, JobStatus? status, int index = 0, bool schedulerStarted = false)
    {
        var now = DateTime.Now;
        var sb = new StringBuilder();
        var title = string.IsNullOrWhiteSpace(job.Name) ? job.Id : $"{job.Id} · {job.Name}";
        sb.AppendLine(index > 0 ? $"{index}. {title}" : $"- {title}");
        sb.AppendLine($"  类型: {DescribeTaskType(job)}");
        sb.AppendLine($"  状态: {DescribeState(job, status, now, schedulerStarted)}；下次触发: {DescribeNextFire(job, status, now, schedulerStarted)}");
        sb.AppendLine($"  调度: {DescribeSchedule(job.Schedule)}");
        sb.AppendLine($"  {(string.Equals(job.TaskType, ScheduleTaskTypes.Text, StringComparison.OrdinalIgnoreCase) ? "内容" : "指令")}: {DescribeContent(job)}");

        if (!string.Equals(job.TaskType, ScheduleTaskTypes.Text, StringComparison.OrdinalIgnoreCase))
        {
            var agent = string.IsNullOrWhiteSpace(job.Agent) ? "默认" : job.Agent;
            var extra = new List<string> { $"Agent: {agent}" };
            if (!string.IsNullOrWhiteSpace(job.Model))
                extra.Add($"模型: {job.Model}");
            if (!string.IsNullOrWhiteSpace(job.Mode))
                extra.Add($"模式: {job.Mode}");
            sb.AppendLine($"  {string.Join("；", extra)}");
        }

        if (!string.IsNullOrWhiteSpace(job.Dispatch?.Target?.SessionId))
            sb.AppendLine($"  目标会话: {job.Dispatch.Target.SessionId}");

        if (job.Dispatch?.Target?.Conversation is { IsComplete: true } conversation)
        {
            var titleSuffix = string.IsNullOrWhiteSpace(conversation.Title) ? string.Empty : $"（{conversation.Title}）";
            sb.AppendLine($"  出站目标: {conversation.ChannelId}/{conversation.Kind}/{conversation.ExternalId}{titleSuffix}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>最近一次心跳执行的单行摘要；从未执行时返回固定说明。</summary>
    public static string DescribeHeartbeatLastRun(JobExecutionRecord? last)
    {
        if (last is null)
            return "从未执行";

        var status = last.Status switch
        {
            "success" => "成功",
            "failed" => "失败",
            _ => last.Status
        };

        var duration = last.Duration is { } d ? $"（耗时 {(int)Math.Round(d.TotalSeconds)}s）" : string.Empty;
        var error = string.IsNullOrWhiteSpace(last.Error) ? string.Empty : $"；错误: {last.Error}";

        return $"{last.StartedAt:yyyy-MM-dd HH:mm:ss} {status}{duration}{error}";
    }

    private static string DescribeWindows(IReadOnlyList<ScheduleWindow>? windows)
    {
        if (windows is null || windows.Count == 0)
            return string.Empty;

        return string.Join("、", windows.Select(w =>
            $"{NormalizeTime(w.Start, "00:00")}-{NormalizeTime(w.End, "23:59:59")}"));
    }

    private static string NormalizeTime(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

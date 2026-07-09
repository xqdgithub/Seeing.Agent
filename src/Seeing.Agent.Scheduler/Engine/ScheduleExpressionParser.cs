using Quartz;
using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.Scheduler.Engine;

/// <summary>Cron / 间隔 / 一次性调度表达式解析</summary>
public static class ScheduleExpressionParser
{
    /// <summary>解析间隔字符串（如 6h、30m、1h30m）</summary>
    public static TimeSpan ParseInterval(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new FormatException("Interval cannot be empty.");

        value = value.Trim().ToLowerInvariant();
        var total = TimeSpan.Zero;
        var i = 0;

        while (i < value.Length)
        {
            var start = i;
            while (i < value.Length && char.IsDigit(value[i]))
                i++;

            if (i == start)
                throw new FormatException($"Invalid interval: '{value}'");

            if (!int.TryParse(value.AsSpan(start, i - start), out var amount))
                throw new FormatException($"Invalid interval amount in '{value}'");

            if (i >= value.Length)
                throw new FormatException($"Missing unit in interval '{value}'");

            total += value[i] switch
            {
                'h' => TimeSpan.FromHours(amount),
                'm' => TimeSpan.FromMinutes(amount),
                's' => TimeSpan.FromSeconds(amount),
                'd' => TimeSpan.FromDays(amount),
                _ => throw new FormatException($"Unknown interval unit '{value[i]}' in '{value}'")
            };
            i++;
        }

        if (total <= TimeSpan.Zero)
            throw new FormatException($"Interval must be positive: '{value}'");

        return total;
    }

    /// <summary>计算下次执行时间</summary>
    public static DateTime? GetNextOccurrence(ScheduleSpec schedule, DateTime from, string? fallbackTimezone = null)
    {
        var tz = ResolveTimeZone(schedule.Timezone ?? fallbackTimezone);

        return schedule.Type switch
        {
            ScheduleTypes.Interval => from + ParseInterval(schedule.Every ?? throw new InvalidOperationException("Interval schedule requires 'every'.")),
            ScheduleTypes.Once => schedule.RunAt > from ? schedule.RunAt : null,
            ScheduleTypes.Cron or _ => GetNextCronOccurrence(schedule.Cron ?? throw new InvalidOperationException("Cron schedule requires 'cron'."), from, tz)
        };
    }

    private static DateTime? GetNextCronOccurrence(string cron, DateTime from, TimeZoneInfo tz)
    {
        try
        {
            var expression = new CronExpression(NormalizeCron(cron));
            expression.TimeZone = tz;
            // Quartz 返回 UTC 时间，转为本地 DateTime
            return expression.GetTimeAfter(from.ToUniversalTime())?.LocalDateTime;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>将常见 4/5 字段 cron 规范化为 Quartz 6 字段格式（秒 分 时 日 月 周）</summary>
    /// <remarks>
    /// Quartz cron 格式：秒 分 时 日 月 周 [年]
    /// 注意：Quartz 要求日和周字段中必须有一个是 `?`
    /// </remarks>
    public static string NormalizeCron(string cron)
    {
        var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            6 => FixQuartzCron(cron),  // Quartz 6 字段（秒 分 时 日 月 周）
            7 => cron,  // Quartz 7 字段（带年）
            5 => FixQuartzCron($"0 {cron}"),  // 标准 5 字段（分 时 日 月 周）-> 添加秒
            4 => FixQuartzCron($"0 0 {cron}"),  // 4 字段 -> 添加秒和分
            3 => FixQuartzCron($"0 0 0 {cron}"),  // 3 字段 -> 添加秒、分、时
            _ => throw new FormatException($"Cron must have 3-7 fields, got {parts.Length}: '{cron}'")
        };
    }

    /// <summary>修复 Quartz cron：确保日或周字段有一个是 `?`</summary>
    private static string FixQuartzCron(string cron)
    {
        var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 6)
        {
            // Quartz 要求：日(第4个)和周(第6个)字段必须有一个是 `?`
            var dayOfMonth = parts[3];
            var dayOfWeek = parts[5];
            
            if (dayOfMonth != "?" && dayOfWeek != "?")
            {
                // 将周字段改为 `?`
                parts[5] = "?";
            }
        }
        return string.Join(" ", parts);
    }

    /// <summary>解析时区</summary>
    public static TimeZoneInfo ResolveTimeZone(string? timezoneId)
    {
        if (string.IsNullOrWhiteSpace(timezoneId) ||
            timezoneId.Equals("Local", StringComparison.OrdinalIgnoreCase))
            return TimeZoneInfo.Local;

        if (timezoneId.Equals("UTC", StringComparison.OrdinalIgnoreCase))
            return TimeZoneInfo.Utc;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
    }
}
using Cronos;
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
    public static DateTimeOffset? GetNextOccurrence(ScheduleSpec schedule, DateTimeOffset from, string? fallbackTimezone = null)
    {
        var tz = ResolveTimeZone(schedule.Timezone ?? fallbackTimezone);

        return schedule.Type switch
        {
            ScheduleTypes.Interval => from + ParseInterval(schedule.Every ?? throw new InvalidOperationException("Interval schedule requires 'every'.")),
            ScheduleTypes.Once => schedule.RunAt > from ? schedule.RunAt : null,
            ScheduleTypes.Cron or _ => GetNextCronOccurrence(schedule.Cron ?? throw new InvalidOperationException("Cron schedule requires 'cron'."), from, tz)
        };
    }

    private static DateTimeOffset? GetNextCronOccurrence(string cron, DateTimeOffset from, TimeZoneInfo tz)
    {
        var expression = CronExpression.Parse(NormalizeCron(cron), CronFormat.Standard);
        return expression.GetNextOccurrence(from, tz, inclusive: false);
    }

    /// <summary>将常见 4/5 字段 cron 规范化为 Cronos 5 字段</summary>
    public static string NormalizeCron(string cron)
    {
        var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            5 => cron,
            4 => $"0 {cron}",
            3 => $"0 0 {cron}",
            _ => throw new FormatException($"Cron must have 3-5 fields, got {parts.Length}: '{cron}'")
        };
    }

    /// <summary>解析时区</summary>
    public static TimeZoneInfo ResolveTimeZone(string? timezoneId)
    {
        if (string.IsNullOrWhiteSpace(timezoneId) || timezoneId.Equals("UTC", StringComparison.OrdinalIgnoreCase))
            return TimeZoneInfo.Utc;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}

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

    /// <summary>计算下次执行时间（返回 schedule 时区下的本地墙钟 DateTime，Kind=Unspecified）</summary>
    public static DateTime? GetNextOccurrence(ScheduleSpec schedule, DateTime from, string? fallbackTimezone = null)
    {
        var tz = ResolveTimeZone(schedule.Timezone ?? fallbackTimezone);

        return schedule.Type switch
        {
            ScheduleTypes.Interval => GetNextIntervalOccurrence(schedule, from, tz),
            ScheduleTypes.Once => schedule.RunAt > from ? schedule.RunAt : null,
            ScheduleTypes.Cron or _ => GetNextCronOccurrence(schedule.Cron ?? throw new InvalidOperationException("Cron schedule requires 'cron'."), from, tz)
        };
    }

    private static DateTime? GetNextIntervalOccurrence(ScheduleSpec schedule, DateTime from, TimeZoneInfo tz)
    {
        var every = ParseInterval(schedule.Every ?? throw new InvalidOperationException("Interval schedule requires 'every'."));

        if (schedule.Windows == null || schedule.Windows.Count == 0)
            return from + every;

        if (!ScheduleWindowsValidator.TryNormalize(schedule.Windows, out var windows, out _))
            return null;

        // from 视为该时区墙钟（Unspecified/Local）；不按 UTC 解释
        var fromLocal = DateTime.SpecifyKind(from, DateTimeKind.Unspecified);
        DateTime? best = null;

        // 扫描今天起若干日历日
        var startDate = fromLocal.Date;
        for (var day = 0; day < 3; day++)
        {
            var date = startDate.AddDays(day);
            foreach (var w in windows)
            {
                foreach (var (anchorDate, start, endExclusive) in ExpandWindowSegments(date, w))
                {
                    var candidate = NextAlignedInSegment(fromLocal, anchorDate, start, endExclusive, every);
                    if (candidate == null)
                        continue;
                    if (best == null || candidate < best)
                        best = candidate;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// 展开与 Quartz 一致的触发段：同日一段；跨夜拆为 start→24:00（锚 start）与 00:00→end（锚 00:00）。
    /// endExclusive 为半开上界（日期+时间）。
    /// </summary>
    private static IEnumerable<(DateTime AnchorDate, TimeSpan Start, DateTime EndExclusive)> ExpandWindowSegments(
        DateTime date,
        NormalizedWindow w)
    {
        if (!w.CrossesMidnight)
        {
            var endExclusive = w.End >= ScheduleWindowsValidator.DefaultEnd
                ? date.AddDays(1)
                : date + w.End;
            yield return (date, w.Start, endExclusive);
            yield break;
        }

        // 晚段：date+Start .. next midnight
        yield return (date, w.Start, date.AddDays(1));
        // 早段：次日 00:00 .. 次日+end（锚为次日 00:00）
        var morningDate = date.AddDays(1);
        var morningEnd = w.End >= ScheduleWindowsValidator.DefaultEnd
            ? morningDate.AddDays(1)
            : morningDate + w.End;
        yield return (morningDate, TimeSpan.Zero, morningEnd);
    }

    private static DateTime? NextAlignedInSegment(
        DateTime fromLocal,
        DateTime anchorDate,
        TimeSpan start,
        DateTime endExclusive,
        TimeSpan every)
    {
        var anchor = anchorDate + start;
        if (every <= TimeSpan.Zero)
            return null;

        DateTime first;
        if (fromLocal < anchor)
        {
            first = anchor;
        }
        else
        {
            var elapsed = fromLocal - anchor;
            var n = (long)Math.Ceiling(elapsed.TotalSeconds / every.TotalSeconds);
            // 若恰好落在锚点+ n*every 且 fromLocal 等于该点，需要下一拍
            first = anchor + TimeSpan.FromSeconds(n * every.TotalSeconds);
            if (first <= fromLocal)
                first += every;
        }

        if (first >= endExclusive)
            return null;

        return DateTime.SpecifyKind(first, DateTimeKind.Unspecified);
    }

    private static DateTime? GetNextCronOccurrence(string cron, DateTime from, TimeZoneInfo tz)
    {
        try
        {
            var expression = new CronExpression(NormalizeCron(cron));
            expression.TimeZone = tz;
            // Quartz 返回 UTC 时间，转为本地墙钟
            var utc = expression.GetTimeAfter(from.ToUniversalTime());
            if (utc == null)
                return null;
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc.Value.UtcDateTime, tz);
            return DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
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
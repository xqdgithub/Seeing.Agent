using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.Scheduler.Heartbeat;

/// <summary>活跃时段检查</summary>
public static class ActiveHoursChecker
{
    /// <summary>当前是否在活跃时段内</summary>
    public static bool IsInActiveHours(ActiveHoursOptions? options)
    {
        if (options == null)
            return true;

        var tz = ScheduleExpressionParser.ResolveTimeZone(options.Timezone);
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        var time = TimeOnly.FromDateTime(now.DateTime);

        if (!TimeOnly.TryParse(options.Start, out var start) ||
            !TimeOnly.TryParse(options.End, out var end))
        {
            return true;
        }

        if (start <= end)
            return time >= start && time <= end;

        // 跨午夜：22:00 - 06:00
        return time >= start || time <= end;
    }
}

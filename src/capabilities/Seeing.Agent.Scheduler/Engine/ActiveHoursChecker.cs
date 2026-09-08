using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.Scheduler.Engine;

/// <summary>活跃时段检查器</summary>
public static class ActiveHoursChecker
{
    /// <summary>检查当前时间是否在活跃时段内</summary>
    public static bool IsInActiveHours(ActiveHoursOptions? activeHours)
    {
        if (activeHours == null)
            return true;  // 无限制

        try
        {
            var now = DateTime.Now;
            
            // 解析时区
            var tz = string.IsNullOrEmpty(activeHours.Timezone)
                ? TimeZoneInfo.Local
                : TimeZoneInfo.FindSystemTimeZoneById(activeHours.Timezone);
            
            var localNow = TimeZoneInfo.ConvertTime(now, tz);
            var localTime = localNow.TimeOfDay;

            // 解析开始和结束时间
            var startTime = ParseTime(activeHours.Start);
            var endTime = ParseTime(activeHours.End);

            // 检查是否在活跃时段内
            if (startTime <= endTime)
            {
                // 正常时段：如 08:00 - 22:00
                return localTime >= startTime && localTime <= endTime;
            }
            else
            {
                // 跨午夜时段：如 22:00 - 06:00
                return localTime >= startTime || localTime <= endTime;
            }
        }
        catch
        {
            return true;  // 解析失败时不限制
        }
    }

    private static TimeSpan ParseTime(string time)
    {
        var parts = time.Split(':');
        if (parts.Length >= 2 && int.TryParse(parts[0], out var hours) && int.TryParse(parts[1], out var minutes))
        {
            return new TimeSpan(hours, minutes, 0);
        }
        return TimeSpan.Zero;
    }
}
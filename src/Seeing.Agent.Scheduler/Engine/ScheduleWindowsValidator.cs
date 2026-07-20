using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.Scheduler.Engine;

/// <summary>规范化后的生效时段（本地墙钟 TimeOfDay）</summary>
public readonly record struct NormalizedWindow(TimeSpan Start, TimeSpan End, bool CrossesMidnight);

/// <summary>interval windows 规范化与交叠校验</summary>
public static class ScheduleWindowsValidator
{
    public static readonly TimeSpan DefaultStart = TimeSpan.Zero;
    public static readonly TimeSpan DefaultEnd = new(23, 59, 59);
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    public static bool TryParseTime(string? text, out TimeSpan time, out string? error)
    {
        time = default;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "时间不能为空";
            return false;
        }

        var parts = text.Trim().Split(':');
        if (parts.Length is < 2 or > 3)
        {
            error = $"无效时间格式: '{text}'（应为 HH:mm 或 HH:mm:ss）";
            return false;
        }

        if (!int.TryParse(parts[0], out var hours) || hours is < 0 or > 23)
        {
            error = $"无效小时: '{text}'";
            return false;
        }

        if (!int.TryParse(parts[1], out var minutes) || minutes is < 0 or > 59)
        {
            error = $"无效分钟: '{text}'";
            return false;
        }

        var seconds = 0;
        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[2], out seconds) || seconds is < 0 or > 59)
            {
                error = $"无效秒: '{text}'";
                return false;
            }
        }

        time = new TimeSpan(hours, minutes, seconds);
        return true;
    }

    public static bool TryNormalize(
        IReadOnlyList<ScheduleWindow>? windows,
        out List<NormalizedWindow> normalized,
        out string? error)
    {
        normalized = new List<NormalizedWindow>();
        error = null;

        if (windows == null || windows.Count == 0)
            return true;

        foreach (var w in windows)
        {
            var startText = string.IsNullOrWhiteSpace(w.Start) ? "00:00" : w.Start;
            var endText = string.IsNullOrWhiteSpace(w.End) ? "23:59:59" : w.End;

            if (!TryParseTime(startText, out var start, out error))
                return false;
            if (!TryParseTime(endText, out var end, out error))
                return false;

            normalized.Add(new NormalizedWindow(start, end, start > end));
        }

        if (HasOverlap(normalized, out error))
            return false;

        return true;
    }

    public static bool ValidateForScheduleType(
        string? type,
        IReadOnlyList<ScheduleWindow>? windows,
        out string? error)
    {
        error = null;
        if (windows == null || windows.Count == 0)
            return true;

        if (!string.Equals(type, ScheduleTypes.Interval, StringComparison.OrdinalIgnoreCase))
        {
            error = "windows 仅支持 schedule.type=interval";
            return false;
        }

        return TryNormalize(windows, out _, out error);
    }

    /// <summary>展开为 0–48h 轴上的半开区间 [start, end)，检测相交。</summary>
    private static bool HasOverlap(IReadOnlyList<NormalizedWindow> windows, out string? error)
    {
        error = null;
        var segments = new List<(TimeSpan Start, TimeSpan End)>();

        foreach (var w in windows)
        {
            // 今天 + 明天两套展开，覆盖跨夜与次日早晨交叠
            AddExpanded(segments, w, dayOffset: TimeSpan.Zero);
            AddExpanded(segments, w, dayOffset: Day);
        }

        segments.Sort((a, b) => a.Start.CompareTo(b.Start));

        for (var i = 1; i < segments.Count; i++)
        {
            var prev = segments[i - 1];
            var cur = segments[i];
            // 半开：prev.End == cur.Start 不相交
            if (prev.End > cur.Start)
            {
                error = "生效时段存在交叠";
                return true;
            }
        }

        return false;
    }

    private static void AddExpanded(
        List<(TimeSpan Start, TimeSpan End)> segments,
        NormalizedWindow w,
        TimeSpan dayOffset)
    {
        if (!w.CrossesMidnight)
        {
            // [start, end)；若 end 为 23:59:59，半开到次日 0 会漏最后一秒——
            // 交叠检测用闭合 end 的下一秒或保留 end 为排他上界。
            // 对 23:59:59，半开上界取 24:00，避免与次日 00:00 起点误判为交叠以外的漏洞。
            var endExclusive = w.End >= DefaultEnd ? Day : w.End;
            segments.Add((dayOffset + w.Start, dayOffset + endExclusive));
            return;
        }

        // 跨夜： [start, 24h) + [0, end)
        segments.Add((dayOffset + w.Start, dayOffset + Day));
        var morningEnd = w.End >= DefaultEnd ? Day : w.End;
        segments.Add((dayOffset + Day, dayOffset + Day + morningEnd));
    }
}

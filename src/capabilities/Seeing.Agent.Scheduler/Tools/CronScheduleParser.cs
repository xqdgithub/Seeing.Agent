using Quartz;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Models;
using Seeing.Serialization;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Seeing.Agent.Scheduler.Tools;

/// <summary>
/// 从工具参数解析并校验 schedule 对象。
/// <para>
/// 除标准对象外，还容忍模型常见的其它入参形态：
/// 字符串化的 JSON（<c>"{\"type\":\"cron\",...}"</c>）、裸 cron 表达式（<c>"0 12 * * *"</c>）、
/// 间隔字符串（<c>"6h"</c>）、裸时间（<c>"2026-10-31 12:00:00"</c>）、
/// snake_case / 别名键（<c>run_at</c>、<c>expression</c>、<c>interval</c>、<c>tz</c> 等）。
/// </para>
/// </summary>
internal static class CronScheduleParser
{
    private const int RawPreviewLimit = 200;

    private static readonly string[] s_typeKeys = ["type", "kind", "scheduletype"];
    private static readonly string[] s_cronKeys = ["cron", "cronexpression", "cronexpr", "expression"];
    private static readonly string[] s_everyKeys = ["every", "interval", "frequency", "period"];
    private static readonly string[] s_runAtKeys = ["runat", "at", "time", "startat", "date", "datetime"];
    private static readonly string[] s_timezoneKeys = ["timezone", "tz", "timeZone"];
    private static readonly string[] s_windowsKeys = ["windows", "window", "activehours"];

    private static readonly Regex s_intervalRegex = new(@"^\d+\s*(s|m|h|d)(\s*\d+\s*(s|m|h|d))*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_offsetSuffixRegex = new(@"([zZ]|[+-]\d{2}:?\d{2})$", RegexOptions.Compiled);

    /// <summary>形如 2026-12-31 / 2026-12-31 12:00:00 / 2026/12/31T12:00 的日期时间字面量。</summary>
    private static readonly Regex s_dateLikeRegex = new(@"^\d{4}[-/]\d{1,2}[-/]\d{1,2}([T\s].*)?$", RegexOptions.Compiled);

    public const string ExamplesText =
        """
        示例：
        - cron: {"type":"cron","cron":"0 9 * * *","timezone":"Asia/Shanghai"}
        - interval: {"type":"interval","every":"6h"}
        - interval+windows: {"type":"interval","every":"40m","timezone":"Asia/Shanghai","windows":[{"start":"09:00","end":"23:00"}]}
        - once: {"type":"once","runAt":"2026-07-20T10:00:00"}
        也接受等价的简写：schedule = "0 9 * * *"（cron）、"6h"（interval）、"2026-07-20 10:00:00"（once）。
        """;

    public static bool TryParse(JsonElement scheduleElement, out ScheduleSpec? schedule, out string? error)
    {
        schedule = null;
        error = null;

        if (scheduleElement.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            error = "schedule 参数是必需的。\n" + ExamplesText;
            return false;
        }

        if (!TryNormalizeRoot(scheduleElement, out var root, out error))
            return false;

        var spec = ReadSpec(root, out error);
        if (spec is null)
            return false;

        if (!TryValidate(spec, out error))
            return false;

        schedule = spec;
        return true;
    }

    /// <summary>把对象/字符串等各种入口形态统一为 JSON 对象。</summary>
    private static bool TryNormalizeRoot(JsonElement element, out JsonElement root, out string? error)
    {
        root = default;
        error = null;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                root = element;
                return true;

            case JsonValueKind.String:
                return TryNormalizeFromString(element.GetString(), out root, out error);

            default:
                error = $"无效的 schedule 参数：期望对象或字符串，实际收到 {element.ValueKind}（{Preview(element.GetRawText())}）。\n{ExamplesText}";
                return false;
        }
    }

    private static bool TryNormalizeFromString(string? text, out JsonElement root, out string? error)
    {
        root = default;
        error = null;

        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            error = "schedule 参数是必需的。\n" + ExamplesText;
            return false;
        }

        // 字符串化的 JSON 对象
        if (value.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(value);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    error = $"无效的 schedule 参数：字符串内容不是 JSON 对象（{Preview(value)}）。\n{ExamplesText}";
                    return false;
                }

                root = doc.RootElement.Clone();
                return true;
            }
            catch (JsonException ex)
            {
                error = $"无效的 schedule 参数：字符串内容不是合法 JSON（{Preview(value)}）。{ex.Message}\n{ExamplesText}";
                return false;
            }
        }

        // 裸时间（含仅有日期的形式）→ once
        if (s_dateLikeRegex.IsMatch(value) && TryParseRunAt(value, TimeZoneInfo.Local, out _))
            return BuildSingleValueObject("runAt", value, out root);

        // 间隔字符串 → interval
        if (s_intervalRegex.IsMatch(value))
            return BuildSingleValueObject("every", value, out root);

        // 其余按 cron 表达式处理
        return BuildSingleValueObject("cron", value, out root);
    }

    private static bool BuildSingleValueObject(string propertyName, string value, out JsonElement root)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(
            new Dictionary<string, string> { [propertyName] = value }, SeeingJson.Readable));
        root = doc.RootElement.Clone();
        return true;
    }

    /// <summary>按别名 + 忽略大小写/下划线/连字符读取字段，组装 <see cref="ScheduleSpec"/>。</summary>
    private static ScheduleSpec? ReadSpec(JsonElement root, out string? error)
    {
        error = null;

        var typeText = ReadString(root, s_typeKeys)?.Trim().ToLowerInvariant();
        var cron = TrimQuotes(ReadString(root, s_cronKeys));
        var every = TrimQuotes(ReadString(root, s_everyKeys));
        var runAtText = TrimQuotes(ReadString(root, s_runAtKeys));
        var timezone = ReadString(root, s_timezoneKeys)?.Trim();
        var hasWindows = TryGetPropertyLoose(root, s_windowsKeys, out var windowsElement)
                         && windowsElement.ValueKind == JsonValueKind.Array
                         && windowsElement.GetArrayLength() > 0;

        if (string.IsNullOrWhiteSpace(typeText))
            typeText = InferType(cron, every, runAtText, hasWindows);

        if (string.IsNullOrWhiteSpace(typeText))
        {
            error = $"无效的 schedule 参数：缺少 type，且无法从 cron/every/runAt 推断（{Preview(root.GetRawText())}）。\n{ExamplesText}";
            return null;
        }

        var spec = new ScheduleSpec
        {
            Type = typeText,
            Cron = string.IsNullOrWhiteSpace(cron) ? null : cron,
            Every = string.IsNullOrWhiteSpace(every) ? null : every,
            Timezone = string.IsNullOrWhiteSpace(timezone) ? TimeZoneInfo.Local.Id : timezone
        };

        if (hasWindows)
        {
            if (!TryReadWindows(windowsElement, out var windows, out error))
                return null;
            spec.Windows = windows;
        }

        if (!string.IsNullOrWhiteSpace(runAtText))
        {
            var tz = ScheduleExpressionParser.ResolveTimeZone(spec.Timezone);
            if (!TryParseRunAt(runAtText, tz, out var runAt))
            {
                error = $"无效的 schedule 参数：无法解析 runAt = '{Preview(runAtText)}'（支持 ISO8601，或 yyyy-MM-dd HH:mm[:ss]）。\n{ExamplesText}";
                return null;
            }

            spec.RunAt = runAt;
        }

        return spec;
    }

    private static string InferType(string? cron, string? every, string? runAt, bool hasWindows)
    {
        if (!string.IsNullOrWhiteSpace(cron))
            return ScheduleTypes.Cron;
        if (!string.IsNullOrWhiteSpace(every))
            return ScheduleTypes.Interval;
        if (!string.IsNullOrWhiteSpace(runAt))
            return ScheduleTypes.Once;

        return hasWindows ? ScheduleTypes.Interval : string.Empty;
    }

    /// <summary>解析一次性执行时间：优先按显式偏移换算到目标时区墙钟，否则按目标时区墙钟解释。</summary>
    private static bool TryParseRunAt(string text, TimeZoneInfo tz, out DateTime value)
    {
        value = default;
        var raw = text.Trim().Trim('"', '\'');

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dto)
            || DateTimeOffset.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out dto))
        {
            value = s_offsetSuffixRegex.IsMatch(raw)
                ? DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(dto, tz).DateTime, DateTimeKind.Unspecified)
                : DateTime.SpecifyKind(dto.DateTime, DateTimeKind.Unspecified);
            return true;
        }

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt)
            || DateTime.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out dt))
        {
            value = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
            return true;
        }

        return false;
    }

    private static bool TryReadWindows(JsonElement element, out List<ScheduleWindow> windows, out string? error)
    {
        windows = new List<ScheduleWindow>();
        error = null;

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString()?.Trim();
                var separator = text?.IndexOfAny(['-', '~']) ?? -1;
                if (string.IsNullOrWhiteSpace(text) || separator <= 0)
                {
                    error = $"无效的 windows 项：'{Preview(text ?? string.Empty)}'（应为 \"HH:mm-HH:mm\" 或 {{start,end}}）。\n{ExamplesText}";
                    return false;
                }

                windows.Add(new ScheduleWindow
                {
                    Start = text![..separator].Trim(),
                    End = text[(separator + 1)..].Trim()
                });
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                error = $"无效的 windows 项：期望对象或 \"HH:mm-HH:mm\" 字符串，实际收到 {item.ValueKind}。\n{ExamplesText}";
                return false;
            }

            windows.Add(new ScheduleWindow
            {
                Start = ReadString(item, ["start", "from", "begin"])?.Trim(),
                End = ReadString(item, ["end", "to", "stop"])?.Trim()
            });
        }

        return true;
    }

    public static bool TryValidate(ScheduleSpec schedule, out string? error)
    {
        error = null;
        var type = schedule.Type?.Trim().ToLowerInvariant();

        try
        {
            switch (type)
            {
                case ScheduleTypes.Cron:
                    if (string.IsNullOrWhiteSpace(schedule.Cron))
                    {
                        error = "cron 类型需要 'cron' 表达式。\n" + ExamplesText;
                        return false;
                    }

                    var normalized = ScheduleExpressionParser.NormalizeCron(schedule.Cron);
                    _ = new CronExpression(normalized);
                    schedule.Type = ScheduleTypes.Cron;
                    if (!ScheduleWindowsValidator.ValidateForScheduleType(ScheduleTypes.Cron, schedule.Windows, out error))
                        return false;
                    break;

                case ScheduleTypes.Interval:
                    if (string.IsNullOrWhiteSpace(schedule.Every))
                    {
                        error = "interval 类型需要 'every'（如 6h、30m）。\n" + ExamplesText;
                        return false;
                    }

                    _ = ScheduleExpressionParser.ParseInterval(schedule.Every);
                    schedule.Type = ScheduleTypes.Interval;
                    if (!ScheduleWindowsValidator.ValidateForScheduleType(ScheduleTypes.Interval, schedule.Windows, out error))
                        return false;
                    break;

                case ScheduleTypes.Once:
                    if (schedule.RunAt is null)
                    {
                        error = "once 类型需要 'runAt'。\n" + ExamplesText;
                        return false;
                    }

                    schedule.Type = ScheduleTypes.Once;
                    if (!ScheduleWindowsValidator.ValidateForScheduleType(ScheduleTypes.Once, schedule.Windows, out error))
                        return false;
                    break;

                default:
                    error = $"不支持的 schedule.type: '{schedule.Type}'。\n{ExamplesText}";
                    return false;
            }
        }
        catch (Exception ex)
        {
            var detail = type == ScheduleTypes.Cron && !string.IsNullOrWhiteSpace(schedule.Cron)
                ? $"cron 表达式 '{schedule.Cron}' 无效：{ex.Message}"
                : ex.Message;
            error = $"无效的调度配置: {detail}\n{ExamplesText}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(schedule.Timezone))
            schedule.Timezone = TimeZoneInfo.Local.Id;

        return true;
    }

    /// <summary>
    /// 当调用方漏传 <c>schedule</c> 对象、把字段平铺在参数顶层时，按别名声成一个等价对象。
    /// 例如 <c>{"cron":"0 12 * * *"}</c> 或 <c>{"runAt":"2026-10-31 12:00:00"}</c>。
    /// </summary>
    public static bool TryBuildFromTopLevel(JsonElement arguments, out JsonElement scheduleElement)
    {
        scheduleElement = default;
        var fields = new Dictionary<string, JsonElement>();

        if (TryGetPropertyLoose(arguments, s_typeKeys, out var type)) fields["type"] = type;
        if (TryGetPropertyLoose(arguments, s_cronKeys, out var cron)) fields["cron"] = cron;
        if (TryGetPropertyLoose(arguments, s_everyKeys, out var every)) fields["every"] = every;
        if (TryGetPropertyLoose(arguments, s_runAtKeys, out var runAt)) fields["runAt"] = runAt;
        if (TryGetPropertyLoose(arguments, s_timezoneKeys, out var timezone)) fields["timezone"] = timezone;
        if (TryGetPropertyLoose(arguments, s_windowsKeys, out var windows)) fields["windows"] = windows;

        if (fields.Count == 0)
            return false;

        // 只有 timezone/windows 不足以构成调度配置，仍需 type 或具体规则。
        var hasRule = fields.ContainsKey("type")
                      || fields.ContainsKey("cron")
                      || fields.ContainsKey("every")
                      || fields.ContainsKey("runAt")
                      || fields.ContainsKey("windows");
        if (!hasRule)
            return false;

        scheduleElement = JsonSerializer.SerializeToElement(fields);
        return true;
    }

    /// <summary>按别名集合读取字符串字段（忽略大小写、下划线、连字符）。</summary>
    private static string? ReadString(JsonElement element, string[] keys)
    {
        if (!TryGetPropertyLoose(element, keys, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static bool TryGetPropertyLoose(JsonElement element, string[] keys, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in element.EnumerateObject())
        {
            var normalized = NormalizeKey(property.Name);
            foreach (var key in keys)
            {
                if (string.Equals(normalized, NormalizeKey(key), StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        return false;
    }

    private static string NormalizeKey(string key) =>
        key.Replace("_", string.Empty).Replace("-", string.Empty).Trim();

    private static string? TrimQuotes(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"', '\'').Trim();

    private static string Preview(string text) =>
        text.Length <= RawPreviewLimit ? text : text[..RawPreviewLimit] + "…";
}

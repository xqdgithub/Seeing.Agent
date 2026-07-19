using System.Text.Json;
using Quartz;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.Scheduler.Tools;

/// <summary>从工具参数解析并校验 schedule 对象。</summary>
internal static class CronScheduleParser
{
    private static readonly System.Text.Json.JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public const string ExamplesText =
        """
        示例：
        - cron: {"type":"cron","cron":"0 9 * * *","timezone":"Asia/Shanghai"}
        - interval: {"type":"interval","every":"6h"}
        - once: {"type":"once","runAt":"2026-07-20T10:00:00"}
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

        ScheduleSpec? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ScheduleSpec>(scheduleElement.GetRawText(), s_jsonOptions);
        }
        catch (JsonException ex)
        {
            error = $"无效的 schedule 参数: {ex.Message}\n{ExamplesText}";
            return false;
        }

        if (parsed is null)
        {
            error = "schedule 参数是必需的。\n" + ExamplesText;
            return false;
        }

        if (!TryValidate(parsed, out error))
            return false;

        schedule = parsed;
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
                    break;

                case ScheduleTypes.Interval:
                    if (string.IsNullOrWhiteSpace(schedule.Every))
                    {
                        error = "interval 类型需要 'every'（如 6h、30m）。\n" + ExamplesText;
                        return false;
                    }

                    _ = ScheduleExpressionParser.ParseInterval(schedule.Every);
                    schedule.Type = ScheduleTypes.Interval;
                    break;

                case ScheduleTypes.Once:
                    if (schedule.RunAt is null)
                    {
                        error = "once 类型需要 'runAt'。\n" + ExamplesText;
                        return false;
                    }

                    schedule.Type = ScheduleTypes.Once;
                    break;

                default:
                    error = $"不支持的 schedule.type: '{schedule.Type}'。\n{ExamplesText}";
                    return false;
            }
        }
        catch (Exception ex)
        {
            error = $"无效的调度配置: {ex.Message}\n{ExamplesText}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(schedule.Timezone))
            schedule.Timezone = TimeZoneInfo.Local.Id;

        return true;
    }
}

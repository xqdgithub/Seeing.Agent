using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Tools.Support;
using System.Text.Json;

namespace Seeing.Agent.Scheduler.Tools;

/// <summary>创建或替换定时任务（text|agent；SessionId 绑定自 ToolContext）。</summary>
public sealed class CronCreateTool : ToolBase
{
    private readonly IScheduleManager _manager;

    public CronCreateTool(ILogger<CronCreateTool> logger, IScheduleManager manager) : base(logger)
    {
        _manager = manager;
    }

    public override string Id => "cron_create";

    public override string Description =>
        "创建或替换定时任务（cron、interval、once）；会话默认取自当前会话。\n\n" +
        "- taskType：text 到点投递固定文案；agent 到点运行 Agent。\n" +
        "- schedule：type 选 cron、interval 或 once；interval 用 every（如 6h、30m），once 用 runAt；timezone 默认本地。\n" +
        "  也接受字符串简写：\"0 9 * * *\"（cron）、\"6h\"（interval）、\"2026-07-20 10:00:00\"（once）。\n" +
        "- windows（仅 interval）：生效时段，空表示全天，允许跨午夜、禁止交叠。\n" +
        "- conversation（可选，默认不填）：出站目标，channelId/kind/externalId 齐备才生效；优先于会话出站绑定。\n" +
        "  默认省略即可：保存时会从目标会话的出站绑定自动快照，无需额外查询或补齐。\n" +
        "  仅当用户明确要求「发到某个群/某个会话」时才填写。\n" +
        "- model（仅 agent）：Native 用 modelId，ACP 用 provider/model；mode 仅 ACP。\n" +
        "- 返回会回显任务名称、类型、调度规则、下次触发时间、提醒内容与出站目标。";

    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            taskType = new
            {
                type = "string",
                description = "任务类型：text（固定文案提醒）| agent（运行 Agent）。必填。"
            },
            id = new { type = "string", description = "任务 ID；省略则自动生成 job_ 前缀 ID" },
            name = new { type = "string", description = "任务显示名称" },
            prompt = new
            {
                type = "string",
                description = "text：用户看到的提醒正文；agent：给 Agent 的任务指令"
            },
            agent = new { type = "string", description = "Agent ID（仅 taskType=agent 时有效）" },
            conversation = new
            {
                type = "object",
                description = "可选出站目标。默认不要填：省略即从目标会话的出站绑定自动快照。仅当用户明确指定群/会话时才填，且 channelId/kind/externalId 需齐备。",
                properties = new
                {
                    channelId = new { type = "string", description = "通道 ID（如 qq / wecom）" },
                    kind = new { type = "string", description = "会话种类：direct（私聊）| group（群聊）| guild（频道）" },
                    externalId = new { type = "string", description = "通道侧会话 ID（群号/用户 ID/频道 ID）" },
                    title = new { type = "string", description = "可选显示名" }
                },
                required = new[] { "channelId", "kind", "externalId" }
            },
            model = new { type = "string", description = "模型 ID（仅 agent）：Native agent 用 modelId；ACP agent 用 provider/model（如 openai/gpt-4）" },
            mode = new { type = "string", description = "ACP 透传 Mode（仅 agent 且 ACP 时有效，如 build / ask）" },
            schedule = new
            {
                type = "object",
                description = "调度配置：type=cron|interval|once；时间均为 timezone 墙钟（非 UTC）。也接受字符串简写：\"0 9 * * *\" / \"6h\" / \"2026-07-20 10:00:00\"",
                properties = new
                {
                    type = new { type = "string", description = "调度类型：cron | interval | once" },
                    cron = new { type = "string", description = "Cron 表达式（type=cron）" },
                    every = new { type = "string", description = "间隔，如 6h、30m（type=interval）" },
                    runAt = new { type = "string", description = "一次性执行时间 ISO8601 本地墙钟（type=once）" },
                    timezone = new { type = "string", description = "时区 ID，默认本地" },
                    windows = new
                    {
                        type = "array",
                        description = "可选生效时段（仅 interval）。空=全天。start 缺省 00:00，end 缺省 23:59:59；允许跨午夜；禁止交叠。均为 timezone 墙钟。",
                        items = new
                        {
                            type = "object",
                            description = "单个生效时段：start 与 end 均为 timezone 墙钟时间",
                            properties = new
                            {
                                start = new { type = "string", description = "HH:mm 或 HH:mm:ss" },
                                end = new { type = "string", description = "HH:mm 或 HH:mm:ss" }
                            }
                        }
                    }
                },
                required = new[] { "type" }
            }
        },
        required = new[] { "taskType", "prompt", "schedule" }
    });

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var prompt = GetStringArgument(arguments, "prompt")?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
            return Failure("prompt 参数是必需的");

        if (!arguments.TryGetProperty("schedule", out var scheduleElement)
            && !CronScheduleParser.TryBuildFromTopLevel(arguments, out scheduleElement))
        {
            return Failure("schedule 参数是必需的（也可直接给 cron / every / runAt）。\n" + CronScheduleParser.ExamplesText);
        }

        if (!CronScheduleParser.TryParse(scheduleElement, out var schedule, out var parseError) || schedule is null)
            return Failure(parseError ?? ("无效的调度配置。\n" + CronScheduleParser.ExamplesText));

        var taskTypeRaw = GetStringArgument(arguments, "taskType")?.Trim();
        if (string.IsNullOrWhiteSpace(taskTypeRaw))
            return Failure("taskType 参数是必需的，取值：text（固定文案提醒）或 agent（运行 Agent）");

        var taskType = taskTypeRaw.ToLowerInvariant();
        if (taskType != ScheduleTaskTypes.Text && taskType != ScheduleTaskTypes.Agent)
            return Failure("无效的 taskType，取值：text（固定文案提醒）或 agent（运行 Agent）");

        var id = GetStringArgument(arguments, "id")?.Trim();
        if (string.IsNullOrWhiteSpace(id))
            id = "job_" + Guid.NewGuid().ToString("N")[..8];

        var isText = taskType == ScheduleTaskTypes.Text;

        // 出站目标：显式 conversation 优先；省略/留空则由 ScheduleManager 从会话绑定快照。
        ConversationSpec? conversation = null;
        if (arguments.TryGetProperty("conversation", out var conversationElement)
            && !CronConversationParser.TryParse(conversationElement, out conversation, out var conversationError))
        {
            return Failure(conversationError ?? "无效的 conversation 参数");
        }

        var job = new ScheduledJobSpec
        {
            Id = id,
            Name = GetStringArgument(arguments, "name")?.Trim(),
            TaskType = taskType,
            Intent = ScheduleIntent.Active,
            Text = isText ? prompt : null,
            Prompt = isText ? null : prompt,
            Agent = isText ? null : GetStringArgument(arguments, "agent")?.Trim(),
            Model = isText ? null : GetStringArgument(arguments, "model")?.Trim(),
            Mode = isText ? null : GetStringArgument(arguments, "mode")?.Trim(),
            Schedule = schedule,
            Dispatch = new DispatchSpec
            {
                Target = new DispatchTarget
                {
                    SessionId = context.SessionId,
                    Conversation = conversation
                }
            }
        };

        try
        {
            var existed = false;
            try
            {
                existed = await _manager.GetJobAsync(id, context.CancellationToken).ConfigureAwait(false) is not null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "查询定时任务是否存在失败（按新建处理）: {JobId}", id);
            }

            var created = await _manager.CreateOrReplaceJobAsync(job, context.CancellationToken).ConfigureAwait(false);

            JobStatus? status = null;
            try
            {
                status = await _manager.GetJobStatusAsync(created.Id, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "查询定时任务状态失败（返回中省略下次触发时间）: {JobId}", created.Id);
            }

            var title = existed ? $"已更新定时任务: {created.Id}" : $"已创建定时任务: {created.Id}";
            return Success(title, ScheduleTextFormatter.DescribeJob(created, status, schedulerStarted: _manager.IsStarted));
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }
}

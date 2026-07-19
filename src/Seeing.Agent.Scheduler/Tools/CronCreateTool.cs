using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.Scheduler.Tools;

/// <summary>创建或替换定时任务（仅 agent 类型；SessionId 绑定自 ToolContext）。</summary>
public sealed class CronCreateTool : ToolBase
{
    private readonly IScheduleManager _manager;

    public CronCreateTool(ILogger<CronCreateTool> logger, IScheduleManager manager) : base(logger)
    {
        _manager = manager;
    }

    public override string Id => "cron_create";

    public override string Description =>
        "创建或替换定时任务（agent）。参数：prompt（必填）、schedule（cron/interval/once），可选 id/name/agent。" +
        " SessionId 自动取自当前会话。";

    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            id = new { type = "string", description = "任务 ID；省略则自动生成 job_ 前缀 ID" },
            name = new { type = "string", description = "任务显示名称" },
            prompt = new { type = "string", description = "Agent 执行的提示词" },
            agent = new { type = "string", description = "Agent ID（可选）" },
            schedule = new
            {
                type = "object",
                description = "调度配置：type=cron|interval|once，配合 cron/every/runAt/timezone",
                properties = new
                {
                    type = new { type = "string", description = "cron | interval | once" },
                    cron = new { type = "string", description = "Cron 表达式（type=cron）" },
                    every = new { type = "string", description = "间隔，如 6h、30m（type=interval）" },
                    runAt = new { type = "string", description = "一次性执行时间 ISO8601（type=once）" },
                    timezone = new { type = "string", description = "时区 ID，默认本地" }
                },
                required = new[] { "type" }
            }
        },
        required = new[] { "prompt", "schedule" }
    });

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var prompt = GetStringArgument(arguments, "prompt")?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
            return Failure("prompt 参数是必需的");

        if (!arguments.TryGetProperty("schedule", out var scheduleElement))
            return Failure("schedule 参数是必需的。\n" + CronScheduleParser.ExamplesText);

        if (!CronScheduleParser.TryParse(scheduleElement, out var schedule, out var parseError) || schedule is null)
            return Failure(parseError ?? ("无效的调度配置。\n" + CronScheduleParser.ExamplesText));

        var id = GetStringArgument(arguments, "id")?.Trim();
        if (string.IsNullOrWhiteSpace(id))
            id = "job_" + Guid.NewGuid().ToString("N")[..8];

        var job = new ScheduledJobSpec
        {
            Id = id,
            Name = GetStringArgument(arguments, "name")?.Trim(),
            Prompt = prompt,
            Agent = GetStringArgument(arguments, "agent")?.Trim(),
            TaskType = ScheduleTaskTypes.Agent,
            Intent = ScheduleIntent.Active,
            Schedule = schedule,
            Dispatch = new DispatchSpec
            {
                Target = new DispatchTarget
                {
                    SessionId = context.SessionId
                }
            }
        };

        try
        {
            var created = await _manager.CreateOrReplaceJobAsync(job, context.CancellationToken).ConfigureAwait(false);
            return Success($"已创建定时任务: {created.Id} [{created.Schedule.Type}]");
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }
}

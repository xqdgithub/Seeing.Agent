using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Tools.Support;
using System.Text.Json;

namespace Seeing.Agent.Scheduler.Tools;

/// <summary>立即触发指定定时任务一次。</summary>
public sealed class CronRunTool : ToolBase
{
    private readonly IScheduleManager _manager;

    public CronRunTool(ILogger<CronRunTool> logger, IScheduleManager manager) : base(logger)
    {
        _manager = manager;
    }

    public override string Id => "cron_run";

    public override string Description =>
        "立即执行一次指定定时任务，不影响后续调度。\n\n" +
        "- 一次性触发，不改变计划时间。";

    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            id = new { type = "string", description = "定时任务 ID" }
        },
        required = new[] { "id" }
    });

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var id = GetStringArgument(arguments, "id")?.Trim();
        if (string.IsNullOrWhiteSpace(id))
            return Failure("id 参数是必需的");

        try
        {
            var job = await _manager.GetJobAsync(id, context.CancellationToken).ConfigureAwait(false);
            var result = await _manager.RunJobOnceAsync(id, context.CancellationToken).ConfigureAwait(false);

            var label = job is null
                ? id
                : string.IsNullOrWhiteSpace(job.Name) ? job.Id : $"{job.Id} · {job.Name}";

            return result switch
            {
                TriggerResult.Accepted a => Success(
                    $"任务已触发: {label}",
                    $"RunId: {a.RunId}" + (job is null ? string.Empty : $"\n调度: {ScheduleTextFormatter.DescribeSchedule(job.Schedule)}")),
                TriggerResult.NotFound => Failure($"任务不存在: {id}"),
                TriggerResult.Disabled => Failure($"任务已禁用，请先 cron_resume: {label}"),
                TriggerResult.Conflict c => Failure(c.Reason),
                _ => Failure("未知触发结果。")
            };
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }
}

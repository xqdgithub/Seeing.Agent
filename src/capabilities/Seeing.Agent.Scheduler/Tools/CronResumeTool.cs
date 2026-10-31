using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Tools.Support;
using System.Text.Json;

namespace Seeing.Agent.Scheduler.Tools;

/// <summary>重新启用已禁用（或已暂停）的定时任务。</summary>
public sealed class CronResumeTool : ToolBase
{
    private readonly IScheduleManager _manager;

    public CronResumeTool(ILogger<CronResumeTool> logger, IScheduleManager manager) : base(logger)
    {
        _manager = manager;
    }

    public override string Id => "cron_resume";

    public override string Description =>
        "重新启用已禁用的定时任务。\n\n" +
        "- 恢复为 Active；需要停止调度用 `cron_disable`。";

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
            if (job is null)
                return Failure($"任务不存在: {id}");

            await _manager.ResumeJobAsync(id, context.CancellationToken).ConfigureAwait(false);

            JobStatus? status = null;
            try
            {
                status = await _manager.GetJobStatusAsync(id, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "查询定时任务状态失败（恢复结果中省略下次触发时间）: {JobId}", id);
            }

            var label = string.IsNullOrWhiteSpace(job.Name) ? job.Id : $"{job.Id} · {job.Name}";
            var expired = JobExpiry.IsExpired(job, status, DateTime.Now, _manager.IsStarted);
            var hint = expired ? "\n注意: 该任务已过期，需编辑调度时间后才会再次触发。" : string.Empty;
            return Success(
                $"已启用任务: {label}",
                $"调度: {ScheduleTextFormatter.DescribeSchedule(job.Schedule)}\n下次触发: {ScheduleTextFormatter.DescribeNextFire(job, status, schedulerStarted: _manager.IsStarted)}{hint}");
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }
}

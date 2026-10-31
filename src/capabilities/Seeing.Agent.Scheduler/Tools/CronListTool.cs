using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Tools.Support;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Scheduler.Tools;

/// <summary>列出所有定时任务及其意图、调度规则、下次触发时间与提醒内容。</summary>
public sealed class CronListTool : ToolBase
{
    private readonly IScheduleManager _manager;

    public CronListTool(ILogger<CronListTool> logger, IScheduleManager manager) : base(logger)
    {
        _manager = manager;
    }

    public override string Id => "cron_list";

    public override string Description =>
        "列出全部定时任务；无需参数。\n\n" +
        "- 每个任务返回名称、任务类型、状态、调度规则、下次触发时间、提醒内容/Agent 指令与目标会话。";

    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { }
    });

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        try
        {
            var jobs = await _manager.ListJobsAsync(context.CancellationToken).ConfigureAwait(false);
            if (jobs.Count == 0)
                return Success("当前没有定时任务。");

            var sb = new StringBuilder();
            sb.AppendLine($"共 {jobs.Count} 个定时任务：");
            sb.AppendLine();

            for (var i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                JobStatus? status = null;
                try
                {
                    status = await _manager.GetJobStatusAsync(job.Id, context.CancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "查询定时任务状态失败（列表中省略下次触发时间）: {JobId}", job.Id);
                }

                sb.AppendLine(ScheduleTextFormatter.DescribeJob(job, status, i + 1, _manager.IsStarted));
                sb.AppendLine();
            }

            return Success("定时任务列表", sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }
}

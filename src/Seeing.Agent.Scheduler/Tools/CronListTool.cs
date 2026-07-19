using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Scheduler.Abstractions;

namespace Seeing.Agent.Scheduler.Tools;

/// <summary>列出所有定时任务及其意图与下次触发时间。</summary>
public sealed class CronListTool : ToolBase
{
    private readonly IScheduleManager _manager;

    public CronListTool(ILogger<CronListTool> logger, IScheduleManager manager) : base(logger)
    {
        _manager = manager;
    }

    public override string Id => "cron_list";

    public override string Description =>
        "列出所有定时任务，包含 intent 与下次触发时间。无需参数。";

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
            foreach (var j in jobs)
            {
                var status = await _manager.GetJobStatusAsync(j.Id, context.CancellationToken).ConfigureAwait(false);
                var nextText = status.NextFireTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
                sb.AppendLine($"- {j.Id} [{j.TaskType}] intent={j.Intent} agent={j.Agent ?? "-"} next={nextText}");
            }

            return Success("定时任务列表", sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }
}

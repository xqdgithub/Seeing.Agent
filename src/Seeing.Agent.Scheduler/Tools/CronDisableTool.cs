using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Scheduler.Abstractions;

namespace Seeing.Agent.Scheduler.Tools;

/// <summary>禁用指定定时任务。</summary>
public sealed class CronDisableTool : ToolBase
{
    private readonly IScheduleManager _manager;

    public CronDisableTool(ILogger<CronDisableTool> logger, IScheduleManager manager) : base(logger)
    {
        _manager = manager;
    }

    public override string Id => "cron_disable";

    public override string Description =>
        "禁用指定定时任务（Intent=Disabled，从调度中移除；可用 cron_resume 重新启用）。参数 id 为任务 ID。";

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

            await _manager.DisableJobAsync(id, context.CancellationToken).ConfigureAwait(false);
            return Success($"已禁用任务: {id}");
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }
}

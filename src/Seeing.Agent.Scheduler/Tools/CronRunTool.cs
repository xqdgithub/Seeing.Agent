using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Models;

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
        "立即执行一次指定定时任务。参数 id 为任务 ID。";

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
            var result = await _manager.RunJobOnceAsync(id, context.CancellationToken).ConfigureAwait(false);
            return result switch
            {
                TriggerResult.Accepted a => Success($"任务已触发，RunId: {a.RunId}"),
                TriggerResult.NotFound => Failure("任务不存在。"),
                TriggerResult.Disabled => Failure("任务已禁用。"),
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

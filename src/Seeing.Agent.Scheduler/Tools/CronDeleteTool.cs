using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Scheduler.Abstractions;

namespace Seeing.Agent.Scheduler.Tools;

/// <summary>删除指定定时任务。</summary>
public sealed class CronDeleteTool : ToolBase
{
    private readonly IScheduleManager _manager;

    public CronDeleteTool(ILogger<CronDeleteTool> logger, IScheduleManager manager) : base(logger)
    {
        _manager = manager;
    }

    public override string Id => "cron_delete";

    public override string Description =>
        "删除指定定时任务。参数 id 为任务 ID。此操作不可恢复。";

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
            var deleted = await _manager.DeleteJobAsync(id, context.CancellationToken).ConfigureAwait(false);
            if (!deleted)
                return Failure($"任务不存在或删除失败: {id}");

            return Success($"已删除任务: {id}");
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }
}

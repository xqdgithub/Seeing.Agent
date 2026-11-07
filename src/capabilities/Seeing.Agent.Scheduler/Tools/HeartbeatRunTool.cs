using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Tools.Support;
using System.Text.Json;

namespace Seeing.Agent.Scheduler.Tools;

/// <summary>立即执行一次心跳。</summary>
public sealed class HeartbeatRunTool : ToolBase
{
    private readonly IHeartbeatService _service;

    public HeartbeatRunTool(ILogger<HeartbeatRunTool> logger, IHeartbeatService service) : base(logger)
    {
        _service = service;
    }

    public override string Id => "heartbeat_run";

    public override string Description =>
        "立即执行一次心跳，不影响后续周期调度；无需参数。\n\n" +
        "- 未配置、未启用、调度器未启动或正在执行时会说明具体原因，不会假装已触发。\n" +
        "- 结果会写回目标会话并进入执行历史。";

    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { }
    });

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        try
        {
            var result = await _service.RunNowAsync(context.CancellationToken).ConfigureAwait(false);

            return result switch
            {
                TriggerResult.Accepted a => Success(
                    "心跳已触发",
                    $"RunId: {a.RunId}\n结果会在执行结束后写入目标会话。"),
                TriggerResult.NotFound => Failure("心跳未配置，请先用 heartbeat_update 设置。"),
                TriggerResult.Disabled => Failure("心跳未启用或调度器未启动，无法立即执行。"),
                TriggerResult.Conflict c => Failure($"心跳正在执行中: {c.Reason}"),
                _ => Failure("未知触发结果。")
            };
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }
}

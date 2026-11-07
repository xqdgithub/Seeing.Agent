using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Tools.Support;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Scheduler.Tools;

/// <summary>读取心跳配置、下次触发时间与最近一次执行结果。</summary>
public sealed class HeartbeatGetTool : ToolBase
{
    private readonly IHeartbeatService _service;

    public HeartbeatGetTool(ILogger<HeartbeatGetTool> logger, IHeartbeatService service) : base(logger)
    {
        _service = service;
    }

    public override string Id => "heartbeat_get";

    public override string Description =>
        "读取心跳的当前配置、是否启用、下次触发时间与最近一次执行结果；无需参数。\n\n" +
        "- 心跳是独立的周期任务，不会出现在 cron_list 的结果里。\n" +
        "- 未配置时返回「未配置」，此时用 heartbeat_update 建立配置。";

    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { }
    });

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        try
        {
            var snapshot = await _service.GetAsync(context.CancellationToken).ConfigureAwait(false);

            if (!snapshot.Configured)
            {
                return Success(
                    "心跳未配置",
                    "心跳尚未配置。用 heartbeat_update 设置 prompt、every 与目标会话后才会开始周期触发。");
            }

            var spec = snapshot.Spec!;
            var sb = new StringBuilder();
            sb.AppendLine(ScheduleTextFormatter.DescribeJob(
                spec, snapshot.Status, schedulerStarted: snapshot.SchedulerStarted));
            sb.AppendLine($"  启用: {(snapshot.Enabled ? "是" : "否")}");
            sb.AppendLine($"  调度器: {(snapshot.SchedulerStarted ? "已启动" : "未启动（不会自动触发）")}");
            sb.AppendLine($"  上次执行: {ScheduleTextFormatter.DescribeHeartbeatLastRun(snapshot.LastRun)}");

            return Success("心跳状态", sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }
}

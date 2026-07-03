using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Acp.Tools;

/// <summary>
/// 查询后台 ACP 任务状态。
/// </summary>
public sealed class AcpStatusTool : ToolBase
{
    private readonly AcpTool _acpTool;

    public AcpStatusTool(ILogger<AcpStatusTool> logger, AcpTool acpTool) : base(logger)
    {
        _acpTool = acpTool;
    }

    public override string Id => "acp_status";

    public override string Description =>
        "查询后台 ACP 任务状态。传入 acp 工具返回的 task_id。";

    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            task_id = new
            {
                type = "string",
                description = "后台任务 ID"
            }
        },
        required = new[] { "task_id" }
    });

    public override Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var taskId = GetStringArgument(arguments, "task_id");
        if (taskId == null)
            return Task.FromResult(Failure("task_id 参数是必需的"));

        var state = _acpTool.GetBackgroundTask(taskId);
        if (state == null)
            return Task.FromResult(Failure($"未找到后台任务: {taskId}"));

        var output = new StringBuilder();
        output.AppendLine($"task_id: {taskId}");
        output.AppendLine($"status: {state.Status}");
        output.AppendLine($"backend: {state.Backend}");
        output.AppendLine($"description: {state.Description}");

        if (!string.IsNullOrWhiteSpace(state.Error))
            output.AppendLine($"error: {state.Error}");

        if (state.Status == "completed" && !string.IsNullOrWhiteSpace(state.Output))
        {
            output.AppendLine();
            output.AppendLine("<task_result>");
            output.AppendLine(state.Output);
            output.AppendLine("</task_result>");
        }

        return Task.FromResult(Success($"ACP 任务 {taskId}", output.ToString(), new Dictionary<string, object>
        {
            ["task_id"] = taskId,
            ["status"] = state.Status
        }));
    }
}

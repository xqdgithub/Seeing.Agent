using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Background;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Session.Core;

namespace Seeing.Agent.Tools.BuiltIn.SubTask;

/// <summary>
/// 查询后台 task 状态；wait=true 时可阻塞至完成或超时。
/// Job 不存在时回落到 Child Session 消息摘要（§4.4）。
/// </summary>
public class TaskStatusTool : ToolBase
{
    private readonly IBackgroundTaskManager _backgroundTasks;
    private readonly ISessionManager _sessionManager;

    public TaskStatusTool(
        ILogger<TaskStatusTool> logger,
        IBackgroundTaskManager backgroundTasks,
        ISessionManager sessionManager) : base(logger)
    {
        _backgroundTasks = backgroundTasks;
        _sessionManager = sessionManager;
    }

    public override string Id => "task_status";

    public override string Description =>
        "查询后台子任务状态。传入 task 返回的 task_id。wait=true 时可阻塞等待完成。";

    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            task_id = new { type = "string", description = "任务 ID（Child Session Id）" },
            wait = new { type = "boolean", description = "是否阻塞等待完成（默认 false）" },
            timeout_ms = new { type = "integer", description = "等待超时毫秒（默认 600000）" }
        },
        required = new[] { "task_id" }
    });

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var taskId = GetStringArgument(arguments, "task_id");
        if (taskId == null)
            return Failure("task_id 参数是必需的");

        var wait = GetBoolArgument(arguments, "wait") ?? false;
        var timeoutMs = 600000;
        if (arguments.TryGetProperty("timeout_ms", out var t) && t.TryGetInt32(out var ms))
            timeoutMs = Math.Clamp(ms, 1, 600_000);

        BackgroundTaskInfo? info;
        if (wait)
        {
            info = await _backgroundTasks.WaitAsync(taskId, timeoutMs);
            if (info == null)
            {
                var current = await _backgroundTasks.GetAsync(taskId);
                if (current == null)
                    return await FallbackFromSessionAsync(taskId);
                if (current.Status is BackgroundTaskStatus.Pending or BackgroundTaskStatus.Running)
                    return Success("timeout", $"task_id: {taskId}\nstate: timeout");
                info = current;
            }
        }
        else
        {
            info = await _backgroundTasks.GetAsync(taskId);
            if (info == null)
                return await FallbackFromSessionAsync(taskId);
        }

        var state = MapJobState(info.Status);
        var sb = new StringBuilder();
        sb.AppendLine($"task_id: {taskId}");
        sb.AppendLine($"state: {state}");
        if (!string.IsNullOrEmpty(info.Result))
        {
            sb.AppendLine();
            sb.AppendLine("<task_result>");
            sb.AppendLine(info.Result);
            sb.AppendLine("</task_result>");
        }
        else if (!string.IsNullOrEmpty(info.Error))
        {
            sb.AppendLine();
            sb.AppendLine("<task_error>");
            sb.AppendLine(info.Error);
            sb.AppendLine("</task_error>");
        }

        return Success(state, sb.ToString(), new Dictionary<string, object>
        {
            ["task_id"] = taskId,
            ["state"] = state
        });
    }

    private async Task<ToolResult> FallbackFromSessionAsync(string taskId)
    {
        var session = _sessionManager.Get(taskId) ?? await _sessionManager.LoadAsync(taskId);
        if (session == null || session.Kind != SessionKind.SubAgent)
            return Success("not_found", $"task_id: {taskId}\nstate: not_found");

        var lastAssistant = session.Messages
            .LastOrDefault(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase));
        var lastError = session.Messages
            .LastOrDefault(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase)
                                && m.Content?.Contains("error", StringComparison.OrdinalIgnoreCase) == true);

        var state = session.Status switch
        {
            SessionStatus.Error => "error",
            SessionStatus.Active => "running",
            _ when lastAssistant != null => "completed",
            _ => "running"
        };

        var sb = new StringBuilder();
        sb.AppendLine($"task_id: {taskId}");
        sb.AppendLine($"state: {state}");
        sb.AppendLine($"source: session");

        if (lastAssistant != null && !string.IsNullOrEmpty(lastAssistant.Content))
        {
            sb.AppendLine();
            sb.AppendLine("<task_result>");
            sb.AppendLine(lastAssistant.Content);
            sb.AppendLine("</task_result>");
        }
        else if (lastError != null && !string.IsNullOrEmpty(lastError.Content))
        {
            sb.AppendLine();
            sb.AppendLine("<task_error>");
            sb.AppendLine(lastError.Content);
            sb.AppendLine("</task_error>");
        }

        return Success(state, sb.ToString(), new Dictionary<string, object>
        {
            ["task_id"] = taskId,
            ["state"] = state,
            ["source"] = "session"
        });
    }

    private static string MapJobState(BackgroundTaskStatus status) => status switch
    {
        BackgroundTaskStatus.Pending => "running",
        BackgroundTaskStatus.Running => "running",
        BackgroundTaskStatus.Completed => "completed",
        BackgroundTaskStatus.Failed => "error",
        BackgroundTaskStatus.Cancelled => "cancelled",
        _ => "running"
    };
}

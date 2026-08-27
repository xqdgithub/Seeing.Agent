using Seeing.Agent.Abstractions.Tools;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Execution;
using Seeing.Session.Core;

namespace Seeing.Agent.Tools.BuiltIn.SubTask;

/// <summary>
/// 查询后台 task 状态；wait=true 时可阻塞至完成或超时。
/// 执行记录不存在时回落到 Child Session 消息摘要（§4.4）。
/// </summary>
[ToolCapability(ToolCapabilityKeys.TimeoutSkip, "true")]
[ToolCapability(ToolCapabilityKeys.CacheEnabled, "false")]
public class TaskStatusTool : ToolBase
{
    private readonly ISessionManager _sessionManager;

    public TaskStatusTool(
        ILogger<TaskStatusTool> logger,
        ISessionManager sessionManager) : base(logger)
    {
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
            timeout_ms = new { type = "integer", description = "等待超时毫秒（默认 600000，正整数）" }
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
        if (arguments.TryGetProperty("timeout_ms", out var t) && t.TryGetInt32(out var ms) && ms > 0)
            timeoutMs = ms;

        // 运行期解析执行引擎，避免与 ToolManager 的构造期循环依赖
        var execService = context.Services?.GetService(typeof(ExecutionJobService)) as ExecutionJobService;
        if (execService == null)
            return Failure("执行引擎不可用，无法查询任务状态");

        var overview = execService.GetOverview(taskId);
        var current = overview.CurrentExecution;

        // 无活跃/排队执行：回落到子会话消息摘要（兼容已完成落盘会话与旧后台任务）
        if (current == null && overview.QueueLength == 0)
            return await FallbackFromSessionAsync(taskId);

        if (wait)
        {
            // 取消优先：父 Loop 取消时 wait 立即返回，而非继续轮询到超时
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
            try
            {
                await execService.WaitForExecutionAsync(current!.ExecutionId, cts.Token);
            }
            catch (OperationCanceledException)
            {
                if (context.CancellationToken.IsCancellationRequested)
                    return Failure("已取消");
                return Success("timeout", $"task_id: {taskId}\nstate: timeout");
            }

            current = execService.GetExecution(current!.ExecutionId);
            if (current == null)
                return await FallbackFromSessionAsync(taskId);
        }

        var state = MapExecutionState(current!.Status);
        var sb = new StringBuilder();
        sb.AppendLine($"task_id: {taskId}");
        sb.AppendLine($"state: {state}");

        var session = _sessionManager.Get(taskId) ?? await _sessionManager.LoadAsync(taskId);
        var lastAssistant = session?.GetActiveMessages()
            .LastOrDefault(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                                && !m.IsSummary);

        if (current.Status == ExecutionStatus.Completed && lastAssistant != null &&
            !string.IsNullOrEmpty(lastAssistant.Content))
        {
            sb.AppendLine();
            sb.AppendLine("<task_result>");
            sb.AppendLine(lastAssistant.Content);
            sb.AppendLine("</task_result>");
        }
        else if (!string.IsNullOrEmpty(current.ErrorMessage))
        {
            sb.AppendLine();
            sb.AppendLine("<task_error>");
            sb.AppendLine(current.ErrorMessage);
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

        // 任务状态基于活跃消息（已压缩的旧回复不参与状态判定）
        var activeMessages = session.GetActiveMessages();
        var lastAssistant = activeMessages
            .LastOrDefault(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase));
        var lastError = activeMessages
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

    private static string MapExecutionState(ExecutionStatus status) => status switch
    {
        ExecutionStatus.Pending => "running",
        ExecutionStatus.Queued => "running",
        ExecutionStatus.Running => "running",
        ExecutionStatus.Completed => "completed",
        ExecutionStatus.Failed => "error",
        ExecutionStatus.Cancelled => "cancelled",
        _ => "running"
    };
}

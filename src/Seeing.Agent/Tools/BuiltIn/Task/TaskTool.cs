using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Agents;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Reminders;
using Seeing.Agent.Core.Scheduling;
using Seeing.Agent.Execution;
using Seeing.Agent.Models;
using Seeing.Session.Core;

namespace Seeing.Agent.Tools.BuiltIn.SubTask;

/// <summary>
/// 子任务工具 — Session-first：创建/续跑 Child Session 并提交给执行引擎执行。
/// </summary>
[ToolCapability(ToolCapabilityKeys.TimeoutSkip, "true")]
[ToolCapability(ToolCapabilityKeys.CacheEnabled, "false")]
public class TaskTool : ToolBase
{
    private readonly ISessionManager _sessionManager;
    private readonly IAgentRegistry _agentRegistry;
    private readonly IAgentLoopScheduler _loopScheduler;

    public TaskTool(
        ILogger<TaskTool> logger,
        ISessionManager sessionManager,
        IAgentRegistry agentRegistry,
        IAgentLoopScheduler loopScheduler) : base(logger)
    {
        _sessionManager = sessionManager;
        _agentRegistry = agentRegistry;
        _loopScheduler = loopScheduler;
    }

    public override string Id => "task";

    public override string Description => BuildDescription();

    public override JsonElement ParametersSchema => BuildObjectSchema(new Dictionary<string, (string, string, bool, string[]?)>
    {
        ["description"] = ("string", "任务简短描述（3-5 个词）", true, null),
        ["prompt"] = ("string", "Agent 要执行的任务内容", true, null),
        ["subagent_type"] = ("string", "专用 Agent 类型", true, null),
        ["task_id"] = ("string", "任务 ID，用于继续之前的子任务（可选）", false, null),
        ["command"] = ("string", "触发此任务的命令（可选）", false, null),
        ["background"] = ("boolean", "是否在后台运行（可选，默认 false）", false, null),
        ["run_in_background"] = ("boolean", "background 的别名（一期兼容，后续移除）", false, null)
    });

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        if (arguments.TryGetProperty("session_id", out _))
            return Failure("session_id 已废弃，请使用 task_id");

        var description = GetStringArgument(arguments, "description");
        var prompt = GetStringArgument(arguments, "prompt");
        var subagentType = GetStringArgument(arguments, "subagent_type");
        var taskId = GetStringArgument(arguments, "task_id");
        var command = GetStringArgument(arguments, "command");
        var background = GetBoolArgument(arguments, "background")
            ?? GetBoolArgument(arguments, "run_in_background")
            ?? false;

        if (description == null) return Failure("description 参数是必需的");
        if (prompt == null) return Failure("prompt 参数是必需的");
        if (subagentType == null) return Failure("subagent_type 参数是必需的");

        var agentInfo = await _agentRegistry.GetAgentAsync(subagentType);
        if (agentInfo == null)
            return Failure($"未知的 Agent 类型: {subagentType}");

        if (agentInfo.Mode == AgentMode.Primary)
            return Failure($"Agent '{subagentType}' 是主代理，不能作为子任务执行");

        if (agentInfo.Runtime != AgentRuntime.Native)
            return Failure($"Agent '{subagentType}' 不是 Native 运行时，TaskTool 仅支持 Native Agent");

        if (agentInfo.Disabled)
            return Failure($"Agent '{subagentType}' 已禁用");

        // 运行期解析执行引擎，避免 ToolManager → TaskTool → ExecutionJobService
        // → IAgentExecutor → AgentExecutor → ToolManager 的构造期循环依赖
        var execService = context.Services?.GetService(typeof(ExecutionJobService)) as ExecutionJobService;
        if (execService == null)
            return Failure("执行引擎不可用，无法创建子任务");

        try
        {
            SessionData session;
            if (!string.IsNullOrEmpty(taskId))
            {
                session = _sessionManager.Get(taskId)
                    ?? await _sessionManager.LoadAsync(taskId)
                    ?? throw new InvalidOperationException($"未找到 task_id: {taskId}");

                if (session.Kind != SessionKind.SubAgent)
                    return Failure($"task_id '{taskId}' 不是 SubAgent 会话");

                if (!string.Equals(session.ParentSessionId, context.SessionId, StringComparison.Ordinal))
                    return Failure($"task_id '{taskId}' 不属于当前父会话");

                // 快速失败：已有进行中的 Loop 或活跃执行直接拒绝（真正的原子抢占在执行引擎队列内）
                if (_loopScheduler.IsLoopBusy(session.Id) ||
                    execService.GetOverview(session.Id).HasActiveExecution)
                    return Failure($"Task {session.Id} is already running. Use task_status to check progress.");
            }
            else
            {
                var parent = _sessionManager.Get(context.SessionId);
                AgentDefinition? parentDef = null;
                if (!string.IsNullOrEmpty(parent?.SelectedAgent))
                    parentDef = await _agentRegistry.GetAgentAsync(parent.SelectedAgent);

                IReadOnlyList<SessionPermissionRule> parentSnapshot =
                    parent?.PermissionSnapshot ?? new List<SessionPermissionRule>();
                var snapshot = SubagentPermissionDeriver.Derive(
                    parentSnapshot,
                    parentDef,
                    agentInfo);

                session = await _sessionManager.CreateChildAsync(
                    context.SessionId,
                    agentInfo.Name,
                    $"{description} (@{agentInfo.Name})",
                    snapshot);

                // 子 Agent 配置了默认模型则覆盖；否则保留 CreateChild 继承的主会话模型
                if (HasConfiguredModel(agentInfo))
                {
                    session.SelectedModel = agentInfo.Model!.ToString();
                    await _sessionManager.SaveAsync(session.Id);
                }
            }

            context.MetadataSink?.SetMetadata(description, new Dictionary<string, object>
            {
                ["sessionId"] = session.Id,
                ["agent"] = agentInfo.Name,
                ["background"] = background,
                ["originToolCallId"] = context.CallId ?? string.Empty
            });

            var userPrompt = string.IsNullOrEmpty(command)
                ? prompt
                : $"[命令触发: {command}]\n\n{prompt}";

            await _sessionManager.AddMessageAsync(session.Id, new SessionMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Role = "user",
                Content = userPrompt,
                CreatedAt = DateTime.UtcNow
            });

            var submitOptions = new ChatOptions
            {
                AgentId = agentInfo.Name,
                ModelId = session.SelectedModel,
                SkipUserMessagePersist = true,
                // 子代理工具调用默认自动批准（与旧 RunAgentAsync 的 AutoApproveInstance 语义一致）
                AutoApprove = SessionAutoApprove.Enabled
            };

            if (background)
            {
                var parentSessionId = context.SessionId;
                var desc = description;
                var childId = session.Id;

                var submitResult = await execService.SubmitAsync(session.Id,
                    new ChatInput { Text = userPrompt }, submitOptions);
                if (!submitResult.Success || string.IsNullOrEmpty(submitResult.ExecutionId))
                    return Failure(submitResult.Error ?? "子任务提交执行失败");

                var executionId = submitResult.ExecutionId;

                // 后台监听完成 → 通知父会话（复用现有 synthetic 注入语义）
                _ = Task.Run(async () =>
                {
                    var finalStatus = ExecutionStatus.Pending;
                    string? errorMessage = null;
                    try
                    {
                        await foreach (var evt in execService.SubscribeEvents(childId, CancellationToken.None))
                        {
                            if (evt is ExecutionCompleteEvent ce && ce.ExecutionId == executionId)
                            {
                                finalStatus = ce.Status;
                                break;
                            }
                            if (evt is LoopCancelledEvent)
                            {
                                finalStatus = ExecutionStatus.Cancelled;
                                break;
                            }
                            if (evt is ErrorEvent err)
                            {
                                finalStatus = ExecutionStatus.Failed;
                                errorMessage = err.Message;
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // ignore
                    }

                    try
                    {
                        switch (finalStatus)
                        {
                            case ExecutionStatus.Completed:
                                var outputText = ReadChildResult(childId);
                                var completedBody =
                                    $"Background task completed: {desc}\ntask_id: {childId}\nstate: completed\n\n<task_result>\n{outputText}\n</task_result>";
                                await _loopScheduler.InjectSyntheticAsync(
                                    parentSessionId,
                                    SystemReminderRenderer.Wrap(
                                        completedBody,
                                        SystemReminder.Sources.Task,
                                        SystemReminder.Kinds.Completed,
                                        taskId: childId),
                                    BuildReminderMeta(childId, "completed", SystemReminder.Kinds.Completed),
                                    CancellationToken.None);
                                break;

                            case ExecutionStatus.Cancelled:
                                var cancelledBody =
                                    $"Background task cancelled: {desc}\ntask_id: {childId}\nstate: cancelled";
                                await _loopScheduler.InjectSyntheticAsync(
                                    parentSessionId,
                                    SystemReminderRenderer.Wrap(
                                        cancelledBody,
                                        SystemReminder.Sources.Task,
                                        SystemReminder.Kinds.Cancelled,
                                        taskId: childId),
                                    BuildReminderMeta(childId, "cancelled", SystemReminder.Kinds.Cancelled),
                                    CancellationToken.None);
                                break;

                            default:
                                var failedBody =
                                    $"Background task failed: {desc}\ntask_id: {childId}\nstate: error\n\n<task_error>\n{errorMessage ?? "未知错误"}\n</task_error>";
                                await _loopScheduler.InjectSyntheticAsync(
                                    parentSessionId,
                                    SystemReminderRenderer.Wrap(
                                        failedBody,
                                        SystemReminder.Sources.Task,
                                        SystemReminder.Kinds.Failed,
                                        taskId: childId),
                                    BuildReminderMeta(childId, "error", SystemReminder.Kinds.Failed),
                                    CancellationToken.None);
                                break;
                        }

                        await _loopScheduler.TryResumeWhenIdleAsync(parentSessionId, CancellationToken.None);
                    }
                    catch
                    {
                        // ignore
                    }
                }, CancellationToken.None);

                // 父会话取消/关闭时级联取消后台子执行
                if (context.CancellationToken.CanBeCanceled)
                {
                    context.CancellationToken.Register(() =>
                    {
                        try
                        {
                            execService.Cancel(executionId);
                        }
                        catch
                        {
                            // ignore
                        }
                    });
                }

                return Success(description, BuildOutput(session.Id, "running",
                    "Background task started. Continue your current work and call task_status when you need the result."));
            }

            var result = await execService.SubmitAsync(session.Id,
                new ChatInput { Text = userPrompt }, submitOptions);
            if (!result.Success || string.IsNullOrEmpty(result.ExecutionId))
                return Failure(result.Error ?? "子任务提交执行失败");

            // 父 Loop 取消时级联取消子执行
            if (context.CancellationToken.CanBeCanceled)
            {
                context.CancellationToken.Register(() =>
                {
                    try
                    {
                        execService.Cancel(result.ExecutionId);
                    }
                    catch
                    {
                        // ignore
                    }
                });
            }

            await execService.WaitForExecutionAsync(result.ExecutionId, context.CancellationToken);

            // 父 Loop 已取消：子任务虽完成，但终态应标记为 Cancelled，避免父已取消却收到 Completed
            if (context.CancellationToken.IsCancellationRequested)
                return Failure("子任务被取消");

            var outputText = ReadChildResult(session.Id);

            return Success(description, BuildOutput(session.Id, "completed", outputText),
                new Dictionary<string, object>
                {
                    ["sessionId"] = session.Id,
                    ["agent"] = agentInfo.Name
                });
        }
        catch (OperationCanceledException)
        {
            return Failure("子任务被取消");
        }
        catch (Exception ex)
        {
            return Failure(ex, "子任务执行失败");
        }
    }

    /// <summary>
    /// 读取子会话最终结果（最后一条非摘要 assistant 消息的内容）。
    /// </summary>
    private string ReadChildResult(string childId)
    {
        var childSession = _sessionManager.Get(childId);
        var lastAssistant = childSession?.GetActiveMessages()
            .LastOrDefault(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                                && !m.IsSummary);
        var text = lastAssistant?.Content?.Trim();
        return string.IsNullOrEmpty(text) ? "子任务执行完成，无输出内容。" : text;
    }

    private static Dictionary<string, string> BuildReminderMeta(
        string childId, string state, string reminderKind) =>
        new()
        {
            ["task_id"] = childId,
            ["state"] = state,
            [SystemReminder.MetadataKeys.Reminder] = "true",
            [SystemReminder.MetadataKeys.Source] = SystemReminder.Sources.Task,
            [SystemReminder.MetadataKeys.Kind] = reminderKind,
            [SystemReminder.MetadataKeys.TaskId] = childId
        };

    private static string BuildOutput(string taskId, string state, string body) =>
        $"task_id: {taskId}\nstate: {state}\n\n<task_result>\n{body}\n</task_result>";

    private string BuildDescription()
    {
        try
        {
            var taskable = Task.Run(() => _agentRegistry.GetTaskableAgentsAsync()).GetAwaiter().GetResult();
            var agentListText = taskable.Count > 0
                ? string.Join("\n", taskable.Select(a =>
                    $"- {a.Name}: {a.Description ?? "此子代理应仅由用户手动调用"}"))
                : "- 无可用子代理（需 Native 运行时，且非 Primary）";

            return
                "创建子任务并使用专用 Native Agent 执行。" +
                "支持传递 task_id 以继续之前的子任务。" +
                "\n\n可用的子代理类型：\n" + agentListText;
        }
        catch
        {
            return "创建子任务并使用专用 Native Agent 执行。支持传递 task_id 以继续之前的子任务。";
        }
    }

    private static bool HasConfiguredModel(AgentDefinition agent) =>
        agent.Model != null && !string.IsNullOrWhiteSpace(agent.Model.ModelId);

    private static JsonElement BuildObjectSchema(
        Dictionary<string, (string Type, string Description, bool Required, string[]? EnumValues)> properties)
    {
        var props = new Dictionary<string, object>();
        var required = new List<string>();
        foreach (var kvp in properties)
        {
            var prop = new Dictionary<string, object>
            {
                ["type"] = kvp.Value.Type,
                ["description"] = kvp.Value.Description
            };
            if (kvp.Value.EnumValues is { Length: > 0 })
                prop["enum"] = kvp.Value.EnumValues;
            props[kvp.Key] = prop;
            if (kvp.Value.Required)
                required.Add(kvp.Key);
        }

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = props
        };
        if (required.Count > 0)
            schema["required"] = required.ToArray();
        return JsonSerializer.SerializeToElement(schema);
    }
}

using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Agents;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Background;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Reminders;
using Seeing.Agent.Core.Scheduling;
using Seeing.Agent.Core.Session;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Session.Core;

namespace Seeing.Agent.Tools.BuiltIn.SubTask;

/// <summary>
/// 子任务工具 — Session-first：创建/续跑 Child Session 并执行专用 Agent。
/// </summary>
[ToolCapability(ToolCapabilityKeys.TimeoutSkip, "true")]
[ToolCapability(ToolCapabilityKeys.CacheEnabled, "false")]
public class TaskTool : ToolBase
{
    private readonly ISessionManager _sessionManager;
    private readonly IAgentRegistry _agentRegistry;
    private readonly IAgentLoopScheduler _loopScheduler;
    private readonly ITaskEventProjector _projector;
    private readonly ISessionEventBus? _eventBus;

    public TaskTool(
        ILogger<TaskTool> logger,
        ISessionManager sessionManager,
        IAgentRegistry agentRegistry,
        IAgentLoopScheduler loopScheduler,
        ITaskEventProjector projector,
        ISessionEventBus? eventBus = null) : base(logger)
    {
        _sessionManager = sessionManager;
        _agentRegistry = agentRegistry;
        _loopScheduler = loopScheduler;
        _projector = projector;
        _eventBus = eventBus;
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

        // 运行期解析后台任务管理器，避免 ToolManager → TaskTool → BackgroundTaskManager
        // → IAgentExecutor → AgentExecutor → ToolManager 的构造期循环依赖
        var backgroundTasks = context.Services?.GetService(typeof(IBackgroundTaskManager)) as IBackgroundTaskManager;

        TaskProjectionContext? ctxProj = null;
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

                var existing = backgroundTasks == null ? null : await backgroundTasks.GetAsync(session.Id);
                if (existing?.Status is BackgroundTaskStatus.Pending or BackgroundTaskStatus.Running)
                    return Failure($"Task {session.Id} is already running. Use task_status to check progress.");

                // 快速失败：已有进行中的 Loop 直接拒绝（真正的原子抢占在 RunAgentAsync 内）
                if (_loopScheduler.IsLoopBusy(session.Id))
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

            ctxProj = new TaskProjectionContext(
                context.SessionId,
                session.Id,
                context.CallId,
                background,
                agentInfo.Name,
                description);

            await EmitParentAsync(context, _projector.CreateStarted(ctxProj));

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

            if (background)
            {
                var parentSessionId = context.SessionId;
                var proj = ctxProj;
                var desc = description;
                var childId = session.Id;
                var agentName = agentInfo.Name;

                await (backgroundTasks ?? throw new InvalidOperationException(
                    "无法解析后台任务管理器（IBackgroundTaskManager）"))
                    .StartAsync(new BackgroundTaskLaunchArgs
                {
                    TaskId = childId,
                    AgentName = agentName,
                    Description = desc,
                    Input = new ChatMessage { Role = ChatRole.User, Content = userPrompt },
                    Context = new AgentContext
                    {
                        SessionId = childId,
                        MessageId = context.MessageId,
                        ParentSessionId = parentSessionId,
                        CancellationToken = CancellationToken.None
                    },
                    LoopRunner = async ct =>
                    {
                        try
                        {
                            var text = await RunAgentAsync(
                                agentName, childId, userPrompt, context, proj, ct);
                            var completedBody =
                                $"Background task completed: {desc}\ntask_id: {childId}\nstate: completed\n\n<task_result>\n{text}\n</task_result>";
                            await _loopScheduler.InjectSyntheticAsync(
                                parentSessionId,
                                SystemReminderRenderer.Wrap(
                                    completedBody,
                                    SystemReminder.Sources.Task,
                                    SystemReminder.Kinds.Completed,
                                    taskId: childId),
                                BuildReminderMeta(childId, "completed", SystemReminder.Kinds.Completed),
                                ct);
                            await EmitParentAsync(context, _projector.CreateCompleted(proj, text));
                            await _loopScheduler.TryResumeWhenIdleAsync(parentSessionId, ct);
                            return text;
                        }
                        catch (OperationCanceledException)
                        {
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
                            await EmitParentAsync(context,
                                _projector.CreateFailed(proj, "子任务被取消", cancelled: true));
                            await _loopScheduler.TryResumeWhenIdleAsync(parentSessionId, CancellationToken.None);
                            throw;
                        }
                        catch (Exception ex)
                        {
                            var failedBody =
                                $"Background task failed: {desc}\ntask_id: {childId}\nstate: error\n\n<task_error>\n{ex.Message}\n</task_error>";
                            await _loopScheduler.InjectSyntheticAsync(
                                parentSessionId,
                                SystemReminderRenderer.Wrap(
                                    failedBody,
                                    SystemReminder.Sources.Task,
                                    SystemReminder.Kinds.Failed,
                                    taskId: childId),
                                BuildReminderMeta(childId, "error", SystemReminder.Kinds.Failed),
                                CancellationToken.None);
                            await EmitParentAsync(context,
                                _projector.CreateFailed(proj, ex.Message));
                            await _loopScheduler.TryResumeWhenIdleAsync(parentSessionId, CancellationToken.None);
                            throw;
                        }
                    }
                });

                // 父会话取消/关闭时级联取消后台子任务
                if (context.CancellationToken.CanBeCanceled)
                {
                    context.CancellationToken.Register(() =>
                    {
                        try
                        {
                            _ = backgroundTasks?.CancelAsync(childId);
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

            var outputText = await RunAgentAsync(
                agentInfo.Name, session.Id, userPrompt, context, ctxProj, context.CancellationToken);

            // 父 Loop 已取消：子任务虽完成，但终态应标记为 Cancelled，避免父已取消却收到 Completed
            if (context.CancellationToken.IsCancellationRequested)
            {
                await EmitParentAsync(context, _projector.CreateFailed(ctxProj, "子任务被取消", cancelled: true));
                return Failure("子任务被取消");
            }

            await EmitParentAsync(context, _projector.CreateCompleted(ctxProj, outputText));

            return Success(description, BuildOutput(session.Id, "completed", outputText),
                new Dictionary<string, object>
                {
                    ["sessionId"] = session.Id,
                    ["agent"] = agentInfo.Name
                });
        }
        catch (OperationCanceledException)
        {
            if (ctxProj != null)
                await EmitParentAsync(context, _projector.CreateFailed(ctxProj, "子任务被取消", cancelled: true));
            return Failure("子任务被取消");
        }
        catch (Exception ex)
        {
            if (ctxProj != null)
                await EmitParentAsync(context, _projector.CreateFailed(ctxProj, ex.Message));
            return Failure(ex, "子任务执行失败");
        }
    }

    private async ValueTask EmitParentAsync(ToolContext context, IMessageEvent evt)
    {
        try
        {
            if (context.EventSink is not null)
                await context.EventSink.EmitAsync(evt);
        }
        catch
        {
            // Channel 可能已关闭（后台完成后）
        }

        try
        {
            var bus = _eventBus
                ?? context.Services?.GetService(typeof(ISessionEventBus)) as ISessionEventBus;
            bus?.Publish(evt.SessionId, evt);
        }
        catch
        {
            // 总线可选
        }
    }

    private async Task<string> RunAgentAsync(
        string agentName,
        string sessionId,
        string prompt,
        ToolContext parentContext,
        TaskProjectionContext projCtx,
        CancellationToken ct)
    {
        var agent = await _agentRegistry.GetAgentAsync(agentName)
            ?? throw new InvalidOperationException($"无法创建 Agent 实例: {agentName}");

        var session = _sessionManager.Get(sessionId);
        var agentContext = new AgentContext
        {
            SessionId = sessionId,
            MessageId = $"msg_{Guid.NewGuid():N}",
            CancellationToken = ct,
            ParentSessionId = parentContext.SessionId,
            PermissionChannel = parentContext.PermissionChannel
                ?? DefaultPermissionChannel.AutoApproveInstance,
            Metadata = new Dictionary<string, object>
            {
                ["parentSessionId"] = parentContext.SessionId
            }
        };

        if (!string.IsNullOrEmpty(session?.SelectedModel))
        {
            agentContext.Metadata[AgentContextKeys.RequestModelId] = session.SelectedModel;
        }

        // 续跑/多轮：子会话已有历史（含压缩摘要锚点）时加载，统一走 GetActiveMessages 契约，
        // 避免子任务续跑上下文丢失；首次创建（空会话）仅传当前指令
        var messages = session is { Messages.Count: > 0 }
            ? ChatMessageHistoryBuilder.BuildHistory(session.GetActiveMessages())
            : new List<ChatMessage>();
        messages.Add(new ChatMessage { Role = ChatRole.User, Content = prompt });

        // 原子抢占 Loop 忙碌标记：同一 child session 的并发提交/续跑只有一个能真正执行，
        // 其余在 TrySetLoopBusy 处直接拒绝（try 前抢占，finally 释放）。
        if (!_loopScheduler.TrySetLoopBusy(sessionId))
            throw new InvalidOperationException($"Task {sessionId} is already running. Use task_status to check progress.");

        try
        {
            var executor = parentContext.Services?.GetService(typeof(IAgentExecutor)) as IAgentExecutor
                ?? throw new InvalidOperationException("无法解析 Agent 执行器（IAgentExecutor）");
            var def = agent;
            var outputBuilder = new StringBuilder();
            await foreach (var evt in executor.ExecuteAsync(def, messages, agentContext, ct))
            {
                foreach (var projected in _projector.Project(evt, projCtx))
                    await EmitParentAsync(parentContext, projected);

                // 子会话无 EventStreamHandler：在此投影并落盘助手/工具消息
                var childSession = _sessionManager.Get(sessionId);
                if (SessionStreamEventApplier.Apply(childSession, evt))
                    await _sessionManager.SaveAsync(sessionId);

                if (evt is StreamCompleteEvent complete &&
                    complete.Message.Role == ChatRole.Assistant &&
                    !string.IsNullOrEmpty(complete.Message.Content))
                {
                    outputBuilder.Clear();
                    outputBuilder.Append(complete.Message.Content);
                }

                if (evt is ErrorEvent error)
                    throw new InvalidOperationException(error.Message);
            }

            var text = outputBuilder.ToString().Trim();
            return string.IsNullOrEmpty(text) ? "子任务执行完成，无输出内容。" : text;
        }
        finally
        {
            _loopScheduler.SetLoopBusy(sessionId, false);
        }
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

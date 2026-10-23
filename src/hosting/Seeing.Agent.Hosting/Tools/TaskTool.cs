using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Reminders;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Abstractions.Sessions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Tools.Support;
using Seeing.Session.Core;
using System.Text;
using System.Text.Json;
using SessionStatus = Seeing.Session.Core.SessionStatus;

namespace Seeing.Agent.Hosting.Tools;

/// <summary>
/// 子任务工具 — Session-first：创建/续跑 Child Session 并提交给执行引擎执行。
/// </summary>
[ToolCapability(ToolCapabilityKeys.TimeoutSkip, "true")]
[ToolCapability(ToolCapabilityKeys.CacheEnabled, "false")]
public class TaskTool : ToolBase
{
    private readonly ISessionManager _sessionManager;
    private readonly ISessionGroupManager _groupManager;
    private readonly IAgentRegistry _agentRegistry;
    private readonly ISessionRuntime _runtime;
    private string? _cachedDescription;

    public TaskTool(
        ILogger<TaskTool> logger,
        ISessionManager sessionManager,
        ISessionGroupManager groupManager,
        IAgentRegistry agentRegistry,
        ISessionRuntime runtime,
        IExecutionEventPublisher eventPublisher) : base(logger)
    {
        _sessionManager = sessionManager;
        _groupManager = groupManager;
        _agentRegistry = agentRegistry;
        _runtime = runtime;
        // 后台监听已改为轮询 WaitForIdleAsync（摆脱事件流回放依赖），
        // 保留 eventPublisher 构造参数以维持 DI 注册与既有测试调用点不变。
        _ = eventPublisher;
    }

    public override string Id => "task";

    /// <summary>同步契约回退描述；异步温暖缓存完成前返回。</summary>
    private const string Headline = "创建或续跑子代理任务；需要并行推进时放后台执行。";

    private const string Body = "传 task_id 续跑已有子任务，不传则新建；等待结果用 `task_status`。";

    private const string FallbackDescription = Headline + "\n\n" + Body;

    public override string Description => _cachedDescription ?? FallbackDescription;

    public override JsonElement ParametersSchema => BuildObjectSchema(new Dictionary<string, (string, string, bool, string[]?)>
    {
        ["description"] = ("string", "任务简短描述（3-5 个词）", true, null),
        ["prompt"] = ("string", "Agent 要执行的任务内容", true, null),
        ["subagent_type"] = ("string", "子代理类型，取描述末尾动态列出的可用值（如 explore、general）", true, null),
        ["task_id"] = ("string", "【重要】不传此参数 = 创建新任务；传入任务ID = 继续该任务。请勿传空字符串。", false, null),
        ["command"] = ("string", "触发此任务的命令（可选）", false, null),
        ["background"] = ("boolean", "是否在后台运行（可选，默认 false）；需要并行推进时设为 true", false, null),
        ["run_in_background"] = ("boolean", "background 的兼容别名，后续移除", false, null)
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

        try
        {
            // 统一取得父会话：GetOrLoadAsync 避免缓存未命中退化为 FollowGlobal；
            // 新建与续跑分支共用，用于 AutoApprove 继承与权限快照派生
            var parent = await _sessionManager.GetOrLoadAsync(context.SessionId, context.CancellationToken);

            SessionData session;
            if (arguments.TryGetProperty("task_id", out _))
            {
                // 显式传入了 task_id 参数
                if (string.IsNullOrEmpty(taskId))
                    return Failure("task_id 参数不能为空字符串，不传此参数以创建新任务");

                session = await _sessionManager.TryGetOrLoadAsync(taskId)
                    ?? throw new InvalidOperationException($"未找到 task_id: {taskId}");

                if (session.Kind != SessionKind.SubAgent)
                    return Failure($"task_id '{taskId}' 不是 SubAgent 会话");

                if (!string.Equals(
                        await _groupManager.GetParentAsync(session.Id, context.CancellationToken),
                        context.SessionId,
                        StringComparison.Ordinal))
                    return Failure($"task_id '{taskId}' 不属于当前父会话");

                // 快速失败：已有进行中的 Loop 或活跃执行直接拒绝（真正的原子抢占在执行引擎队列内）
                if (_runtime.GetOverview(session.Id).HasActiveExecution)
                    return Failure($"Task {session.Id} is already running. Use task_status to check progress.");
            }
            else
            {
                AgentDefinition? parentDef = null;
                if (!string.IsNullOrEmpty(parent?.SelectedAgent))
                    parentDef = await _agentRegistry.GetAgentAsync(parent.SelectedAgent);

                IReadOnlyList<SessionPermissionRule> parentSnapshot =
                    parent?.PermissionSnapshot ?? new List<SessionPermissionRule>();
                var snapshot = SubagentPermissionDeriver.Derive(
                    parentSnapshot,
                    parentDef,
                    agentInfo);

                session = await _groupManager.CreateChildAsync(
                    context.SessionId,
                    agentInfo.Name,
                    $"{description} (@{agentInfo.Name})",
                    snapshot,
                    parent?.Scenario);

                // 子 Agent 配置了默认模型则覆盖；否则保留 CreateChild 继承的主会话模型
                if (HasConfiguredModel(agentInfo))
                {
                    session.SelectedModel = agentInfo.Model!.ToString();
                    await _sessionManager.SaveAsync(session.Id);
                }
            }

            // 关联元数据：写入子会话，UI 据此精确匹配"父 task 工具调用 ↔ 子会话"（多子代理并行）
            if (!session.Metadata.ContainsKey(SessionMetadataKeys.OriginToolCallId))
            {
                session.Metadata[SessionMetadataKeys.OriginToolCallId] = context.CallId ?? string.Empty;
                await _sessionManager.SaveAsync(session.Id);
            }

            var userPrompt = string.IsNullOrEmpty(command)
                ? prompt
                : $"[命令触发: {command}]\n\n{prompt}";

            if (background)
            {
                var parentSessionId = context.SessionId;
                var desc = description;
                var childId = session.Id;

                // 子会话 user 消息由 DeliverAsync 统一落盘（不再预落盘）。
                var submitResult = await _runtime.DeliverAsync(new InboxMessage
                {
                    TargetSessionId = session.Id,
                    Sender = new InboxSender { Kind = InboxSenderKind.Session, Id = context.SessionId },
                    Body = userPrompt,
                    Delivery = InboxDelivery.Wake,
                    Presentation = InboxPresentation.User,
                    AgentId = agentInfo.Name,
                    ModelId = session.SelectedModel
                }, context.CancellationToken);
                if (!submitResult.Success)
                    return Failure(submitResult.Error ?? "子任务提交执行失败");

                // 后台监听完成 → 通知父会话。
                // 经 WaitForIdleAsync 轮询子会话空闲，终态从 LastExecution 读取，不再订阅事件流。
                _ = Task.Run(async () =>
                {
                    var finalStatus = ExecutionStatus.Failed;
                    string? errorMessage = null;
                    try
                    {
                        await _runtime.WaitForIdleAsync(childId, CancellationToken.None);

                        var record = _runtime.GetOverview(childId).LastExecution;
                        if (record != null)
                        {
                            finalStatus = record.Status;
                            errorMessage = record.ErrorMessage;
                        }
                    }
                    catch
                    {
                        // 忽略：无法取得终态时按失败处理
                    }

                    try
                    {
                        // 投递判据 = 子自身终止原因：仅当子因父停止级联停止时不投递；
                        // 其它终态（完成/失败/被 interrupt 等）一律投递。
                        var child = await _sessionManager.TryGetOrLoadAsync(childId);
                        if (child == null)
                            return;

                        var stopReason = child.Metadata.TryGetValue(SessionControlKeys.StopReason, out var sr) ? sr : null;
                        if (string.Equals(stopReason, SessionControlKeys.StopReasonCascadedFromParent, StringComparison.Ordinal))
                            return;

                        var owner = child.GetControl().Owner;
                        var (kind, body) = await BuildCompletionReportAsync(
                            parentSessionId, finalStatus, desc, childId, errorMessage);

                        await _runtime.DeliverAsync(new InboxMessage
                        {
                            TargetSessionId = parentSessionId,
                            Sender = new InboxSender { Kind = InboxSenderKind.Session, Id = childId },
                            Body = body,
                            Delivery = owner == SessionOwner.Human ? InboxDelivery.Notify : InboxDelivery.Wake,
                            Presentation = owner == SessionOwner.Human ? InboxPresentation.None : InboxPresentation.System,
                            SourceTag = SystemReminder.Sources.Task,
                            Kind = kind
                        }, CancellationToken.None);
                    }
                    catch
                    {
                        // ignore
                    }
                }, CancellationToken.None);

                return Success(description, BuildOutput(session.Id, "running",
                    $"后台任务已启动，任务ID: {session.Id}。\n\n" +
                    $"【重要】请继续处理其他不依赖此任务的工作。" +
                    $"只有当后续工作必须依赖此任务结果时，才使用task_status的wait=true等待。" +
                    $"系统会在任务完成时自动注入通知。"),
                    BuildTaskMetadata(session.Id, agentInfo.Name, description, background: true));
            }

            var result = await _runtime.DeliverAsync(new InboxMessage
            {
                TargetSessionId = session.Id,
                Sender = new InboxSender { Kind = InboxSenderKind.Session, Id = context.SessionId },
                Body = userPrompt,
                Delivery = InboxDelivery.Wake,
                Presentation = InboxPresentation.User,
                AgentId = agentInfo.Name,
                ModelId = session.SelectedModel
            }, context.CancellationToken);
            if (!result.Success)
                return Failure(result.Error ?? "子任务提交执行失败");

            await _runtime.WaitForIdleAsync(session.Id, context.CancellationToken);

            // 父 Loop 已取消：子任务虽完成，但终态应标记为 Cancelled，避免父已取消却收到 Completed
            if (context.CancellationToken.IsCancellationRequested)
                return Failure("子任务被取消");

            var outputText = ReadChildResult(session.Id);

            return Success(description, BuildOutput(session.Id, "completed", outputText),
                BuildTaskMetadata(session.Id, agentInfo.Name, description, background: false));
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
    /// 读取子会话所有非摘要 assistant 消息的内容。
    /// </summary>
    private string ReadChildResult(string childId)
    {
        var childSession = _sessionManager.Get(childId);
        var allAssistants = childSession?.GetActiveMessages()
            .Where(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                        && !m.IsSummary)
            .Select(m => m.Content?.Trim())
            .Where(content => !string.IsNullOrEmpty(content));

        var result = allAssistants is null ? "" : string.Join("\n\n", allAssistants);
        return string.IsNullOrEmpty(result) ? "子任务执行完成，无输出内容。" : result;
    }

    /// <summary>
    /// 构建后台子任务完成上报的 <c>(Kind, Body)</c>：Body 为任务正文（system-reminder 信封由
    /// <see cref="ISessionRuntime.DeliverAsync"/> 依 SourceTag/Kind 统一构造），Kind 为提醒种类。
    /// </summary>
    private async Task<(string Kind, string Body)> BuildCompletionReportAsync(
        string parentSessionId, ExecutionStatus finalStatus, string desc, string childId, string? errorMessage)
    {
        switch (finalStatus)
        {
            case ExecutionStatus.Completed:
                var outputText = ReadChildResult(childId);
                var statusSummary = await GetSubAgentStatusSummaryAsync(parentSessionId, childId);

                var completedBody = $"后台任务已完成: {desc}\ntask_id: {childId}\nstate: completed";
                if (!string.IsNullOrEmpty(statusSummary))
                    completedBody += statusSummary;
                completedBody += $"\n\n<task_result>\n{outputText}\n</task_result>";

                return (SystemReminder.Kinds.Completed, completedBody);

            case ExecutionStatus.Cancelled:
                return (SystemReminder.Kinds.Cancelled,
                    $"Background task cancelled: {desc}\ntask_id: {childId}\nstate: cancelled");

            default:
                return (SystemReminder.Kinds.Failed,
                    $"Background task failed: {desc}\ntask_id: {childId}\nstate: error\n\n<task_error>\n{errorMessage ?? "未知错误"}\n</task_error>");
        }
    }

    /// <summary>
    /// 构建 task 工具结果的权威元数据（供 ChatEventTracker 映射到 SessionToolCall.Task*）。
    /// </summary>
    private static Dictionary<string, object> BuildTaskMetadata(
        string childId, string agent, string description, bool background) =>
        new()
        {
            [TaskMetadataKeys.TaskId] = childId,
            [TaskMetadataKeys.TaskAgent] = agent,
            [TaskMetadataKeys.TaskDescription] = description,
            [TaskMetadataKeys.TaskBackground] = background
        };

    /// <summary>
    /// 获取父会话的所有子代理状态摘要
    /// </summary>
    private async Task<string> GetSubAgentStatusSummaryAsync(string parentSessionId, string currentChildId)
    {
        var children = await _groupManager.ListChildrenAsync(parentSessionId);
        if (children == null || children.Count <= 1)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("\n## 所有子代理状态");

        var allOthersCompleted = true;
        var runningTasks = new List<(string Id, string Desc)>();
        var completedTasks = new List<(string Id, string Desc)>();
        var failedTasks = new List<(string Id, string Desc)>();

        foreach (var child in children)
        {
            // 当前正在完成的子任务标记为已完成
            if (string.Equals(child.Id, currentChildId, StringComparison.Ordinal))
            {
                completedTasks.Add((child.Id, child.Title ?? child.Id));
                continue;
            }

            var overview = GetExecutionOverview(child.Id);
            var state = child.Status switch
            {
                SessionStatus.Error => "failed",
                SessionStatus.Active when overview.HasActiveExecution => "running",
                SessionStatus.Active => "completed",
                _ => "completed"
            };

            var taskDesc = child.Title ?? child.Id;
            var taskInfo = (child.Id, taskDesc);
            switch (state)
            {
                case "running":
                    runningTasks.Add(taskInfo);
                    allOthersCompleted = false;
                    break;
                case "completed":
                    completedTasks.Add(taskInfo);
                    break;
                case "failed":
                    failedTasks.Add(taskInfo);
                    allOthersCompleted = false;
                    break;
            }
        }

        if (runningTasks.Count > 0)
        {
            sb.AppendLine($"运行中: {runningTasks.Count}");
            foreach (var task in runningTasks)
                sb.AppendLine($"  - {task.Desc} (ID: {task.Id})");
        }

        if (completedTasks.Count > 0)
        {
            sb.AppendLine($"已完成: {completedTasks.Count}");
            foreach (var task in completedTasks)
                sb.AppendLine($"  - {task.Desc} (ID: {task.Id})");
        }

        if (failedTasks.Count > 0)
        {
            sb.AppendLine($"失败: {failedTasks.Count}");
            foreach (var task in failedTasks)
                sb.AppendLine($"  - {task.Desc} (ID: {task.Id})");
        }

        if (allOthersCompleted)
        {
            sb.AppendLine();
            sb.AppendLine("✅ 所有子代理任务已完成，可以继续处理父任务。");
        }

        return sb.ToString();
    }

    private static string BuildOutput(string taskId, string state, string body) =>
        $"task_id: {taskId}\nstate: {state}\n\n<task_result>\n{body}\n</task_result>";

    /// <summary>
    /// 预热工具描述缓存。ITool.Description 为同步契约，getter 内无法 await；
    /// 由 <see cref="HostingModule.ActivateAsync"/> 在注册前异步取回可委托 Agent 列表并缓存。
    /// </summary>
    public async Task WarmDescriptionAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedDescription != null)
            return;

        _cachedDescription = await BuildDescriptionAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> BuildDescriptionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var taskable = await _agentRegistry.GetTaskableAgentsAsync().ConfigureAwait(false);
            // 列表必须有上限：子代理数量不可控，无界追加会让描述突破 R3 的 700 字预算。
            const int maxListed = 8;
            var agentListText = taskable.Count > 0
                ? string.Join("\n", taskable.Take(maxListed).Select(a =>
                    $"- {a.Name}: {Truncate(a.Description ?? "此子代理应仅由用户手动调用", 40)}"))
                  + (taskable.Count > maxListed ? $"\n- …共 {taskable.Count} 个可用子代理" : string.Empty)
                : "- 无可用子代理（需 Native 运行时，且非 Primary）";

            return
                Headline + "\n\n" + Body +
                "\n\n可用子代理类型：\n" + agentListText;
        }
        catch
        {
            return FallbackDescription;
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

    private static bool HasConfiguredModel(AgentDefinition agent) =>        agent.Model != null && !string.IsNullOrWhiteSpace(agent.Model.ModelId);

    /// <summary>
    /// 获取执行状态概览
    /// </summary>
    private SessionExecutionOverview GetExecutionOverview(string sessionId)
    {
        return _runtime.GetOverview(sessionId);
    }

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

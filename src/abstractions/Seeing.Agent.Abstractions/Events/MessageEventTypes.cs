using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Todo;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
namespace Seeing.Agent.Abstractions.Events;

/// <summary>
/// 消息事件类型 - 开放的字符串标识（插件可自由扩展，无需修改本类）
/// </summary>
public static class MessageEventType
{
    /// <summary>Agent Loop 开始（一次完整对话循环开始）</summary>
    public const string LoopStart = "loop.start";

    /// <summary>Agent Loop 结束（一次完整对话循环结束）</summary>
    public const string LoopComplete = "loop.complete";

    /// <summary>流式开始（新轮次开始信号）</summary>
    public const string StreamStart = "stream.start";

    /// <summary>流式增量（实时渲染用）</summary>
    public const string StreamDelta = "stream.delta";

    /// <summary>流式结束（添加历史用）</summary>
    public const string StreamComplete = "stream.complete";

    /// <summary>工具调用请求（pending）</summary>
    public const string ToolCallPending = "tool.call.pending";

    /// <summary>工具执行中</summary>
    public const string ToolCallRunning = "tool.call.running";

    /// <summary>工具执行完成</summary>
    public const string ToolCallComplete = "tool.call.complete";

    /// <summary>权限请求（需要用户确认）</summary>
    public const string PermissionRequest = "permission.request";

    /// <summary>权限结果（授权已判定）</summary>
    public const string PermissionResolved = "permission.resolved";

    /// <summary>问题请求（需要用户作答）</summary>
    public const string QuestionRequest = "question.request";

    /// <summary>问题结果（作答已判定）</summary>
    public const string QuestionResolved = "question.resolved";

    /// <summary>Loop 被取消</summary>
    public const string LoopCancelled = "loop.cancelled";

    /// <summary>错误</summary>
    public const string Error = "error";

    /// <summary>命令执行结果</summary>
    public const string CommandResult = "command.result";

    /// <summary>预算状态更新</summary>
    public const string BudgetStatus = "budget.status";

    /// <summary>预算警告</summary>
    public const string BudgetWarning = "budget.warning";

    /// <summary>导航请求</summary>
    public const string Navigate = "navigate";

    /// <summary>Session 更新</summary>
    public const string SessionUpdated = "session.updated";

    /// <summary>Skill 内容展开</summary>
    public const string SkillContent = "skill.content";

    /// <summary>Session 标题变更</summary>
    public const string SessionTitleChanged = "session.title.changed";

    /// <summary>Todo 列表更新</summary>
    public const string TodoUpdate = "todo.update";

    /// <summary>会话模式更新</summary>
    public const string ModeUpdate = "mode.update";

    /// <summary>Schema 快照（工具与区块可用性）</summary>
    public const string SchemaSnapshot = "schema.snapshot";

    /// <summary>应用层轮次重试已排期（UI 展示“X 秒后重试”）</summary>
    public const string LlmRetry = "llm.retry";
}

/// <summary>
/// CompactionEvents 专用常量（点分，与子项目①约定一致）
/// </summary>
public static class CompactionEventTypes
{
    /// <summary>压缩开始</summary>
    public const string Started = "compaction.started";

    /// <summary>压缩增量进度</summary>
    public const string Delta = "compaction.delta";

    /// <summary>压缩完成</summary>
    public const string Completed = "compaction.completed";

    /// <summary>压缩失败</summary>
    public const string Failed = "compaction.failed";
}

/// <summary>
/// Agent Loop 阶段
/// </summary>
public enum LoopPhase
{
    /// <summary>思考阶段（推理/Thinking）</summary>
    Thinking,

    /// <summary>工具调用阶段</summary>
    ToolCalling,

    /// <summary>回复生成阶段</summary>
    Responding,

    /// <summary>已完成</summary>
    Completed
}

/// <summary>
/// 消息事件基接口
/// </summary>
public interface IMessageEvent
{
    /// <summary>会话 ID</summary>
    string SessionId { get; }

    /// <summary>Agent Loop ID（一次完整对话循环的唯一标识）</summary>
    /// <remarks>
    /// LoopId 用于关联一次 Agent 交互中产生的所有事件（思考、工具调用、回复等），
    /// 便于前端按对话单元渲染，避免不同 Loop 的内容交错显示。
    /// </remarks>
    string? LoopId { get; }

    /// <summary>时间戳</summary>
    DateTime Timestamp { get; }

    /// <summary>事件类型（开放字符串标识）</summary>
    string Type { get; }
}

/// <summary>
/// Agent Loop 开始事件 - 标记一次完整对话循环开始
/// </summary>
public record LoopStartEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public required string LoopId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Type => MessageEventType.LoopStart;

    /// <summary>触发 Loop 的用户消息 ID</summary>
    public string? TriggerMessageId { get; init; }

    /// <summary>用户输入内容（简要）</summary>
    public string? UserInput { get; init; }
}

/// <summary>
/// Agent Loop 结束事件 - 标记一次完整对话循环结束
/// </summary>
public record LoopCompleteEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public required string LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Type => MessageEventType.LoopComplete;

    /// <summary>Loop 执行的总步数</summary>
    public int TotalSteps { get; init; }

    /// <summary>Loop 执行的总耗时</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>是否成功完成</summary>
    public bool Success { get; init; }

    /// <summary>错误信息（失败时）</summary>
    public string? Error { get; init; }

    /// <summary>
    /// 结束原因（如 <c>turn-directive</c>：工具要求提前结束本轮）。
    /// <para>成功但非自然结束时填充；自然结束/失败为空。</para>
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>Token 使用统计</summary>
    public TokenUsage? Usage { get; init; }
}

/// <summary>
/// 流式开始事件 - 标记新轮次开始
/// <para>
/// AgentExecutor 多轮循环中，每轮 LLM 调用前发出此事件，
/// 通知 UI 层清空状态，准备接收新一轮的 delta。
/// </para>
/// </summary>
public record StreamStartEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Type => MessageEventType.StreamStart;

    /// <summary>轮次索引（step=0, 1, 2...）</summary>
    public int Step { get; init; }

    /// <summary>
    /// 该轮第几次尝试（从 1 开始）。
    /// <para>契约：同一 (LoopId, Step) 多次发出本事件表示该轮被重开，消费端必须视为“重置”——
    /// 投影层移除该轮未完成 assistant 消息，UI 清空该轮渲染缓冲。</para>
    /// </summary>
    public int Attempt { get; init; } = 1;
}

/// <summary>
/// 流式增量事件 - 用于实时渲染
/// </summary>
public record StreamDeltaEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Type => MessageEventType.StreamDelta;

    /// <summary>内容增量</summary>
    public string? ContentDelta { get; init; }

    /// <summary>推理/思考过程增量</summary>
    public string? ReasoningDelta { get; init; }

    /// <summary>工具调用增量（累积）</summary>
    public List<ToolCall>? ToolCallDeltas { get; init; }

    /// <summary>Token 使用统计</summary>
    public TokenUsage? Usage { get; init; }
}

/// <summary>
/// 流式结束事件 - 用于添加历史
/// </summary>
public record StreamCompleteEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Type => MessageEventType.StreamComplete;

    /// <summary>完整的消息对象</summary>
    public required ChatMessage Message { get; init; }

    /// <summary>Token 使用统计</summary>
    public TokenUsage? Usage { get; init; }
}

/// <summary>
/// 工具调用状态
/// </summary>
public enum ToolCallStatus
{
    /// <summary>请求中</summary>
    Pending,

    /// <summary>执行中</summary>
    Running,

    /// <summary>执行成功</summary>
    Success,

    /// <summary>执行失败</summary>
    Failed,

    /// <summary>被拒绝</summary>
    Rejected,

    /// <summary>已取消（用户取消或超时等导致未完成）</summary>
    Cancelled
}

/// <summary>
/// 工具调用事件
/// </summary>
public record ToolCallEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Type { get; init; } = string.Empty;

    /// <summary>工具调用 ID</summary>
    public required string ToolCallId { get; init; }

    /// <summary>工具名称</summary>
    public required string ToolName { get; init; }

    /// <summary>工具参数</summary>
    public object? Arguments { get; init; }

    /// <summary>工具调用状态</summary>
    public required ToolCallStatus Status { get; init; }

    /// <summary>执行结果输出（仅 Complete 时有）</summary>
    public string? Output { get; init; }

    /// <summary>执行结果标题</summary>
    public string? Title { get; init; }

    /// <summary>错误信息（仅 Failed 时有）</summary>
    public string? Error { get; init; }

    /// <summary>执行耗时</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>工具结果元数据（如 bash 的 exit / timedOut / aborted）</summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }

    /// <summary>工具请求的轮次指令（ToolResult.TurnDirective 透传）</summary>
    public ToolTurnDirective TurnDirective { get; init; } = ToolTurnDirective.Continue;

    /// <summary>轮次指令原因</summary>
    public string? TurnDirectiveReason { get; init; }

    /// <summary>
    /// 回传给模型的内容：成功为 <see cref="Output"/>；失败为带防误判 notice 的
    /// <c>tool_result</c> XML 信封（详见 <see cref="ToolResultFormatting"/>）。
    /// </summary>
    public string ModelContent => ToolResultFormatting.ToModelContent(
        Status == ToolCallStatus.Success,
        Output,
        Error,
        ToolName,
        Status.ToString().ToLowerInvariant(),
        Title);
}

/// <summary>
/// 错误事件
/// </summary>
public record ErrorEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Type => MessageEventType.Error;

    /// <summary>错误信息</summary>
    public required string Message { get; init; }

    /// <summary>错误详情</summary>
    public Exception? Exception { get; init; }

    /// <summary>错误来源（agent/tool/llm/system）</summary>
    public string? Source { get; init; }
}

/// <summary>
/// LLM 轮次重试排期事件 - 应用层决定重试、进入退避等待前发出（不落盘、不进对话历史）。
/// </summary>
public record LlmRetryScheduledEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Type => MessageEventType.LlmRetry;

    /// <summary>轮次索引</summary>
    public int Step { get; init; }

    /// <summary>即将进行的第几次尝试（2、3…）</summary>
    public int Attempt { get; init; }

    /// <summary>本次退避等待时长（毫秒），供 UI 倒计时</summary>
    public int NextDelayMs { get; init; }

    /// <summary>失败简述</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// 命令执行结果事件 - 内置命令执行完成后发出
/// </summary>
public record CommandResultEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string Type => MessageEventType.CommandResult;

    /// <summary>命令名称</summary>
    public required string CommandName { get; init; }

    /// <summary>是否成功</summary>
    public bool Success { get; init; }

    /// <summary>结果消息</summary>
    public string? Message { get; init; }

    /// <summary>导航目标（可选）</summary>
    public string? NavigationTarget { get; init; }

    /// <summary>是否需要前端刷新时间线（压缩等变更会话内容的命令）</summary>
    public bool NeedsRefresh { get; init; }

    /// <summary>
    /// 是否继续执行 Agent（false = 命令要求结束本轮：shouldContinue=false 或 shouldExit）。
    /// 宿主据此短路：不再把命令文本作为普通消息发送给大模型。
    /// </summary>
    public bool ShouldContinue { get; init; } = true;
}

/// <summary>
/// 权限请求事件 - 需要用户确认
/// </summary>
public record PermissionRequestEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Type => MessageEventType.PermissionRequest;

    /// <summary>权限请求 ID</summary>
    public required string RequestId { get; init; }

    /// <summary>关联的工具调用 ID（内联卡片关联键）</summary>
    public string? CallId { get; init; }

    /// <summary>权限类型: tool, file, network, shell, agent</summary>
    public required string PermissionKind { get; init; }

    /// <summary>资源标识（工具名/文件路径等）</summary>
    public string? Resource { get; init; }

    /// <summary>请求参数（JSON）</summary>
    public object? Arguments { get; init; }

    /// <summary>风险等级: low, medium, high, critical</summary>
    public string RiskLevel { get; init; } = "medium";

    /// <summary>提示消息</summary>
    public string? Message { get; init; }

    /// <summary>允许的动作集合（呈现用，由执行级授权器按 kind 注入）</summary>
    public IReadOnlyList<PermissionGrantScope> AllowedScopes { get; init; } = Array.Empty<PermissionGrantScope>();

    /// <summary>超时时间（秒）</summary>
    public int TimeoutSeconds { get; init; } = 300;
}

/// <summary>
/// 权限结果事件 - 授权已判定（用户/策略/取消/超时/无通道）
/// </summary>
public record PermissionResolvedEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Type => MessageEventType.PermissionResolved;

    /// <summary>对应的权限请求 ID</summary>
    public required string RequestId { get; init; }

    /// <summary>关联的工具调用 ID</summary>
    public string? CallId { get; init; }

    /// <summary>决策: Allow, Deny</summary>
    public required PermissionEffect Decision { get; init; }

    /// <summary>决策作用域</summary>
    public PermissionGrantScope Scope { get; init; } = PermissionGrantScope.Once;

    /// <summary>决策来源</summary>
    public PermissionResolvedBy ResolvedBy { get; init; } = PermissionResolvedBy.User;

    /// <summary>决策原因（可选）</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Loop 被取消事件 - 用户主动取消或超时
/// </summary>
public record LoopCancelledEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public required string LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Type => MessageEventType.LoopCancelled;

    /// <summary>取消原因: user, timeout, error, resource_limit</summary>
    public required string Reason { get; init; }

    /// <summary>已完成的步数</summary>
    public int CompletedSteps { get; init; }

    /// <summary>已完成的消息（部分结果）</summary>
    public List<ChatMessage>? PartialMessages { get; init; }

    /// <summary>Token 使用统计（部分）</summary>
    public TokenUsage? PartialUsage { get; init; }
}

/// <summary>
/// Todo 列表更新事件 - Agent 计划变更时发出
/// </summary>
public record TodoUpdateEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Type => MessageEventType.TodoUpdate;

    /// <summary>Todo 列表</summary>
    public List<TodoItem> Todos { get; init; } = new();
}

/// <summary>
/// 会话模式更新事件 - ACP Agent 切换模式时发出
/// </summary>
public record ModeUpdateEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Type => MessageEventType.ModeUpdate;

    /// <summary>新模式 ID</summary>
    public required string ModeId { get; init; }
}
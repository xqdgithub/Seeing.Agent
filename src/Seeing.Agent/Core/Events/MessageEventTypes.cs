namespace Seeing.Agent.Core.Events;

/// <summary>
/// 消息事件类型
/// </summary>
public enum MessageEventType
{
    /// <summary>Agent Loop 开始（一次完整对话循环开始）</summary>
    LoopStart,

    /// <summary>Agent Loop 结束（一次完整对话循环结束）</summary>
    LoopComplete,

    /// <summary>流式开始（新轮次开始信号）</summary>
    StreamStart,

    /// <summary>流式增量（实时渲染用）</summary>
    StreamDelta,

    /// <summary>流式结束（添加历史用）</summary>
    StreamComplete,

    /// <summary>工具调用请求（pending）</summary>
    ToolCallPending,

    /// <summary>工具执行中</summary>
    ToolCallRunning,

    /// <summary>工具执行完成</summary>
    ToolCallComplete,

    /// <summary>子代理启动</summary>
    SubAgentStarted,

    /// <summary>子代理完成</summary>
    SubAgentCompleted,

    /// <summary>权限请求（需要用户确认）</summary>
    PermissionRequest,

    /// <summary>权限响应（用户确认/拒绝）</summary>
    PermissionResponse,

    /// <summary>Loop 被取消（用户主动取消）</summary>
    LoopCancelled,

    /// <summary>错误</summary>
    Error
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

    /// <summary>事件类型</summary>
    MessageEventType Type { get; }
}

/// <summary>
/// Agent Loop 开始事件 - 标记一次完整对话循环开始
/// </summary>
public record LoopStartEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public required string LoopId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public MessageEventType Type => MessageEventType.LoopStart;

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
    public MessageEventType Type => MessageEventType.LoopComplete;

    /// <summary>Loop 执行的总步数</summary>
    public int TotalSteps { get; init; }

    /// <summary>Loop 执行的总耗时</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>是否成功完成</summary>
    public bool Success { get; init; }

    /// <summary>错误信息（失败时）</summary>
    public string? Error { get; init; }

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
    public MessageEventType Type => MessageEventType.StreamStart;

    /// <summary>轮次索引（step=0, 1, 2...）</summary>
    public int Step { get; init; }
}

/// <summary>
/// 流式增量事件 - 用于实时渲染
/// </summary>
public record StreamDeltaEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public MessageEventType Type => MessageEventType.StreamDelta;

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
    public MessageEventType Type => MessageEventType.StreamComplete;

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
    Rejected
}

/// <summary>
/// 工具调用事件
/// </summary>
public record ToolCallEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public MessageEventType Type { get; init; }

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
}

/// <summary>
/// 子代理事件
/// </summary>
public record SubAgentEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public MessageEventType Type { get; init; }

    /// <summary>子代理名称</summary>
    public required string AgentName { get; init; }

    /// <summary>状态（started/completed/failed）</summary>
    public required string Status { get; init; }

    /// <summary>子会话 ID</summary>
    public string? SubSessionId { get; init; }

    /// <summary>执行结果（Completed 时有）</summary>
    public string? Result { get; init; }

    /// <summary>错误信息（Failed 时有）</summary>
    public string? Error { get; init; }
}

/// <summary>
/// 错误事件
/// </summary>
public record ErrorEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public MessageEventType Type => MessageEventType.Error;

    /// <summary>错误信息</summary>
    public required string Message { get; init; }

    /// <summary>错误详情</summary>
    public Exception? Exception { get; init; }

    /// <summary>错误来源（agent/tool/llm/system）</summary>
    public string? Source { get; init; }
}

/// <summary>
/// 权限请求事件 - 需要用户确认
/// </summary>
public record PermissionRequestEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public MessageEventType Type => MessageEventType.PermissionRequest;

    /// <summary>权限请求 ID</summary>
    public required string PermissionId { get; init; }

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

    /// <summary>超时时间（秒）</summary>
    public int TimeoutSeconds { get; init; } = 300;
}

/// <summary>
/// 权限响应事件 - 用户确认/拒绝
/// </summary>
public record PermissionResponseEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public MessageEventType Type => MessageEventType.PermissionResponse;

    /// <summary>对应的权限请求 ID</summary>
    public required string PermissionId { get; init; }

    /// <summary>用户决策: allow, deny</summary>
    public required string Decision { get; init; }

    /// <summary>决策原因（可选）</summary>
    public string? Reason { get; init; }

    /// <summary>是否记住决策（会话级别）</summary>
    public bool Remember { get; init; }
}

/// <summary>
/// Loop 被取消事件 - 用户主动取消或超时
/// </summary>
public record LoopCancelledEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public required string LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public MessageEventType Type => MessageEventType.LoopCancelled;

    /// <summary>取消原因: user, timeout, error, resource_limit</summary>
    public required string Reason { get; init; }

    /// <summary>已完成的步数</summary>
    public int CompletedSteps { get; init; }

    /// <summary>已完成的消息（部分结果）</summary>
    public List<ChatMessage>? PartialMessages { get; init; }

    /// <summary>Token 使用统计（部分）</summary>
    public TokenUsage? PartialUsage { get; init; }
}
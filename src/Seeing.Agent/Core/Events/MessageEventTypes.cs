namespace Seeing.Agent.Core.Events;

/// <summary>
/// 消息事件类型
/// </summary>
public enum MessageEventType
{
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

    /// <summary>错误</summary>
    Error
}

/// <summary>
/// 消息事件基接口
/// </summary>
public interface IMessageEvent
{
    /// <summary>会话 ID</summary>
    string SessionId { get; }

    /// <summary>时间戳</summary>
    DateTime Timestamp { get; }

    /// <summary>事件类型</summary>
    MessageEventType Type { get; }
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
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public MessageEventType Type => MessageEventType.Error;

    /// <summary>错误信息</summary>
    public required string Message { get; init; }

    /// <summary>错误详情</summary>
    public Exception? Exception { get; init; }

    /// <summary>错误来源（agent/tool/llm/system）</summary>
    public string? Source { get; init; }
}
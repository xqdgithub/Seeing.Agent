using Seeing.Agent.Abstractions.Questions;

namespace Seeing.Agent.Abstractions.Events;

/// <summary>
/// 问题请求事件 - 需要用户作答
/// </summary>
public record QuestionRequestEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Type => MessageEventType.QuestionRequest;

    /// <summary>问题请求 ID</summary>
    public required string RequestId { get; init; }

    /// <summary>关联的工具调用 ID（内联卡片关联键）</summary>
    public string? CallId { get; init; }

    /// <summary>问题摘要</summary>
    public IReadOnlyList<Question> Questions { get; init; } = Array.Empty<Question>();
}

/// <summary>
/// 问题结果事件 - 作答已判定（提交/取消/超时/无通道）
/// </summary>
public record QuestionResolvedEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Type => MessageEventType.QuestionResolved;

    /// <summary>对应的问题请求 ID</summary>
    public required string RequestId { get; init; }

    /// <summary>关联的工具调用 ID（内联卡片关联键）</summary>
    public string? CallId { get; init; }

    /// <summary>结果状态</summary>
    public required QuestionResultStatus Status { get; init; }

    /// <summary>回答列表</summary>
    public IReadOnlyList<QuestionAnswer> Answers { get; init; } = Array.Empty<QuestionAnswer>();
}

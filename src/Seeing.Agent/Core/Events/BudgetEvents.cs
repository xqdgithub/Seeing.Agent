using Seeing.Session.Core;
using Seeing.TokenBudget.Api.Responses;

namespace Seeing.Agent.Core.Events;

/// <summary>
/// 预算状态更新事件 - 通知 UI 更新进度条
/// </summary>
public record BudgetStatusEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public MessageEventType Type => MessageEventType.BudgetStatus;

    /// <summary>当前 Token 数</summary>
    public int CurrentTokens { get; init; }

    /// <summary>最大 Token 数</summary>
    public int MaxTokens { get; init; }

    /// <summary>使用百分比</summary>
    public double UsagePercentage { get; init; }

    /// <summary>预算级别</summary>
    public BudgetLevel Level { get; init; }

    /// <summary>Token 分布详情</summary>
    public TokenBreakdownResponse? Breakdown { get; init; }
}

/// <summary>
/// 压缩执行事件 - 通知 UI 压缩结果
/// </summary>
public record CompactionEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public MessageEventType Type => MessageEventType.Compaction;

    /// <summary>使用的压缩策略</summary>
    public string Strategy { get; init; } = string.Empty;

    /// <summary>压缩前 Token 数</summary>
    public int TokensBefore { get; init; }

    /// <summary>压缩后 Token 数</summary>
    public int TokensAfter { get; init; }

    /// <summary>移除的消息数</summary>
    public int MessagesRemoved { get; init; }

    /// <summary>是否成功</summary>
    public bool Success { get; init; }

    /// <summary>错误信息（失败时）</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// 预算警告事件 - 通知 UI 显示警告
/// </summary>
public record BudgetWarningEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public MessageEventType Type => MessageEventType.BudgetWarning;

    /// <summary>警告消息</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>预算级别</summary>
    public BudgetLevel Level { get; init; }
}

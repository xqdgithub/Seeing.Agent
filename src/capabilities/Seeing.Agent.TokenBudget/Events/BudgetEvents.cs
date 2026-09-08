using Seeing.Agent.Abstractions.Events;
using Seeing.Session.Core;
using Seeing.Agent.TokenBudget.Api.Responses;

namespace Seeing.Agent.TokenBudget;

/// <summary>
/// 预算状态更新事件 - 通知 UI 更新进度条
/// </summary>
public record BudgetStatusEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Type => MessageEventType.BudgetStatus;

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
/// 预算警告事件 - 通知 UI 显示警告
/// </summary>
public record BudgetWarningEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Type => MessageEventType.BudgetWarning;

    /// <summary>警告消息</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>预算级别</summary>
    public BudgetLevel Level { get; init; }
}

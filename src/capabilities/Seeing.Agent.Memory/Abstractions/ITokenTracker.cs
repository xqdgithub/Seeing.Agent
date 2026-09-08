namespace Seeing.Agent.Memory.Abstractions;

/// <summary>
/// Token 追踪接口 - 记录 LLM API 消耗
/// </summary>
public interface ITokenTracker
{
    /// <summary>记录 Token 消耗</summary>
    Task TrackAsync(TokenUsage usage, CancellationToken ct = default);

    /// <summary>获取指定时间段的消耗</summary>
    Task<TokenUsage> GetUsageAsync(
        DateTimeOffset startTime, 
        DateTimeOffset endTime, 
        CancellationToken ct = default);

    /// <summary>获取今日消耗</summary>
    Task<TokenUsage> GetTodayUsageAsync(CancellationToken ct = default);

    /// <summary>获取指定操作的消耗</summary>
    Task<TokenUsage> GetOperationUsageAsync(string operation, CancellationToken ct = default);
}

/// <summary>
/// Token 使用量
/// </summary>
public record TokenUsage(
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    int RequestCount
)
{
    public static TokenUsage Empty => new(0, 0, 0, 0);

    public static TokenUsage operator +(TokenUsage a, TokenUsage b) =>
        new(a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens,
            a.TotalTokens + b.TotalTokens, a.RequestCount + b.RequestCount);
}

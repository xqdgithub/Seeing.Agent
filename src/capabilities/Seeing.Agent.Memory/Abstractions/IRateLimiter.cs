namespace Seeing.Agent.Memory.Abstractions;

/// <summary>
/// 频率限制接口 - Token Bucket 算法
/// </summary>
public interface IRateLimiter
{
    /// <summary>尝试获取令牌</summary>
    Task<bool> TryAcquireAsync(int tokens = 1, CancellationToken ct = default);

    /// <summary>等待获取令牌</summary>
    Task WaitAsync(int tokens = 1, CancellationToken ct = default);

    /// <summary>获取当前状态</summary>
    RateLimitStatus GetStatus();
}

/// <summary>
/// 频率限制状态
/// </summary>
public record RateLimitStatus(
    int AvailableTokens,
    int MaxTokens,
    double RefillRate,
    DateTimeOffset LastRefill
);

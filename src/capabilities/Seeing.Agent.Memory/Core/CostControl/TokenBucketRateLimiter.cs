using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;

namespace Seeing.Agent.Memory.Core.CostControl;

/// <summary>
/// Token Bucket 频率限制器实现
/// </summary>
public class TokenBucketRateLimiter : IRateLimiter
{
    private readonly int _maxTokens;
    private readonly double _refillRate; // tokens per second
    private readonly ILogger<TokenBucketRateLimiter>? _logger;

    private double _tokens;
    private DateTimeOffset _lastRefill;
    private readonly object _lock = new();

    /// <summary>
    /// 创建 TokenBucketRateLimiter 实例
    /// </summary>
    /// <param name="maxTokens">最大令牌数</param>
    /// <param name="refillRate">每秒补充令牌数</param>
    /// <param name="logger">日志记录器</param>
    public TokenBucketRateLimiter(
        int maxTokens = 100,
        double refillRate = 10.0,
        ILogger<TokenBucketRateLimiter>? logger = null)
    {
        _maxTokens = maxTokens;
        _refillRate = refillRate;
        _tokens = maxTokens;
        _lastRefill = DateTimeOffset.UtcNow;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool TryAcquire(int tokens = 1)
    {
        lock (_lock)
        {
            Refill();

            if (_tokens >= tokens)
            {
                _tokens -= tokens;
                _logger?.LogDebug("获取令牌成功: {Tokens}, 剩余: {Remaining}", tokens, _tokens);
                return true;
            }

            _logger?.LogDebug("获取令牌失败: {Tokens}, 剩余: {Remaining}", tokens, _tokens);
            return false;
        }
    }

    /// <inheritdoc />
    public Task<bool> TryAcquireAsync(int tokens = 1, CancellationToken ct = default)
    {
        return Task.FromResult(TryAcquire(tokens));
    }

    /// <inheritdoc />
    public async Task WaitAsync(int tokens = 1, CancellationToken ct = default)
    {
        while (!TryAcquire(tokens))
        {
            ct.ThrowIfCancellationRequested();

            // 计算需要等待的时间
            double waitSeconds;
            lock (_lock)
            {
                waitSeconds = (tokens - _tokens) / _refillRate;
            }

            _logger?.LogDebug("等待令牌: {WaitSeconds:F2}秒", waitSeconds);
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.1, waitSeconds)), ct);
        }
    }

    /// <inheritdoc />
    public RateLimitStatus GetStatus()
    {
        lock (_lock)
        {
            Refill();
            return new RateLimitStatus(
                AvailableTokens: (int)_tokens,
                MaxTokens: _maxTokens,
                RefillRate: _refillRate,
                LastRefill: _lastRefill
            );
        }
    }

    private void Refill()
    {
        var now = DateTimeOffset.UtcNow;
        var elapsed = (now - _lastRefill).TotalSeconds;

        if (elapsed > 0)
        {
            _tokens = Math.Min(_maxTokens, _tokens + elapsed * _refillRate);
            _lastRefill = now;
        }
    }
}

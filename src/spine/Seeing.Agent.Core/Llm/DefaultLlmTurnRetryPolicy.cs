using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Core.Llm;

/// <summary>
/// 应用层轮次重试默认实现：复用 <see cref="LlmRetryPolicy"/> 的可重试判定与退避计算。
/// </summary>
public sealed class DefaultLlmTurnRetryPolicy : ILlmTurnRetryPolicy
{
    private readonly LlmTurnRetryOptions _options;

    /// <summary>
    /// 使用默认重试选项（<see cref="LlmTurnRetryOptions.Default"/>）构造轮次重试策略。
    /// </summary>
    public DefaultLlmTurnRetryPolicy()
        : this(LlmTurnRetryOptions.Default) { }

    /// <summary>
    /// 使用指定重试选项构造轮次重试策略。
    /// </summary>
    public DefaultLlmTurnRetryPolicy(LlmTurnRetryOptions options)
        => _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// 是否启用应用层轮次重试。
    /// </summary>
    public bool Enabled => _options.Enabled;

    /// <summary>
    /// 判定异常是否可重试：LlmException 子类按其 IsRetryable 判定，其余走 LlmRetryPolicy 规则。
    /// </summary>
    public bool CanRetry(Exception ex, CancellationToken cancellationToken)
    {
        // AgentExecutor 拿到的异常是 LlmService 包装后的 LlmException 子类（LlmStreamingException/
        // LlmConnectionException/LlmTimeoutException），它们不是 IOException/OCE，直接交给
        // LlmRetryPolicy.IsRetryable 会恒为 false；必须按 LlmException.IsRetryable 判定。
        if (ex is LlmException llmEx)
            return llmEx.IsRetryable;

        return LlmRetryPolicy.IsRetryable(ex, cancellationToken);
    }

    /// <summary>
    /// 计算指定尝试次数对应的指数退避延迟。
    /// </summary>
    public TimeSpan NextDelay(int attempt)
        => LlmRetryPolicy.ComputeDelay(
            attempt,
            TimeSpan.FromMilliseconds(_options.BaseDelayMs > 0 ? _options.BaseDelayMs : 500),
            TimeSpan.FromMilliseconds(_options.MaxDelayMs > 0 ? _options.MaxDelayMs : 10_000));

    /// <summary>
    /// 综合开关、最大尝试次数与总时间预算判定是否进行下一轮重试。
    /// </summary>
    public bool ShouldRetry(int attempt, TimeSpan elapsed, TimeSpan nextDelay)
    {
        if (!_options.Enabled)
            return false;

        if (_options.MaxAttempts > 0 && attempt >= _options.MaxAttempts)
            return false;

        if (_options.TotalBudgetMs > 0 &&
            elapsed + nextDelay > TimeSpan.FromMilliseconds(_options.TotalBudgetMs))
            return false;

        return true;
    }
}

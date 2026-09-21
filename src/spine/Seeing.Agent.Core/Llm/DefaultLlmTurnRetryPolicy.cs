using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Core.Llm;

/// <summary>
/// 应用层轮次重试默认实现：复用 <see cref="LlmRetryPolicy"/> 的可重试判定与退避计算。
/// </summary>
public sealed class DefaultLlmTurnRetryPolicy : ILlmTurnRetryPolicy
{
    private readonly LlmTurnRetryOptions _options;

    public DefaultLlmTurnRetryPolicy()
        : this(LlmTurnRetryOptions.Default) { }

    public DefaultLlmTurnRetryPolicy(LlmTurnRetryOptions options)
        => _options = options ?? throw new ArgumentNullException(nameof(options));

    public bool Enabled => _options.Enabled;

    public bool CanRetry(Exception ex, CancellationToken cancellationToken)
    {
        // AgentExecutor 拿到的异常是 LlmService 包装后的 LlmException 子类（LlmStreamingException/
        // LlmConnectionException/LlmTimeoutException），它们不是 IOException/OCE，直接交给
        // LlmRetryPolicy.IsRetryable 会恒为 false；必须按 LlmException.IsRetryable 判定。
        if (ex is LlmException llmEx)
            return llmEx.IsRetryable;

        return LlmRetryPolicy.IsRetryable(ex, cancellationToken);
    }

    public TimeSpan NextDelay(int attempt)
        => LlmRetryPolicy.ComputeDelay(
            attempt,
            TimeSpan.FromMilliseconds(_options.BaseDelayMs > 0 ? _options.BaseDelayMs : 500),
            TimeSpan.FromMilliseconds(_options.MaxDelayMs > 0 ? _options.MaxDelayMs : 10_000));

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

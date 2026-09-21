namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 应用层轮次重试策略：决定单次 LLM 调用失败后是否整轮重开（可回滚），与传输层重试分层。
/// </summary>
public interface ILlmTurnRetryPolicy
{
    /// <summary>是否启用轮次重试。</summary>
    bool Enabled { get; }

    /// <summary>异常是否可做轮次重试（与传输层同一 IsRetryable 语义）。</summary>
    bool CanRetry(Exception ex, CancellationToken cancellationToken);

    /// <summary>第 attempt 次失败后的退避延迟（attempt 从 1 开始，指数封顶）。</summary>
    TimeSpan NextDelay(int attempt);

    /// <summary>
    /// 是否继续重试。默认不受次数/时长上限约束（仅 Enabled 约束），
    /// 可选上限字段用于运维收紧。
    /// </summary>
    bool ShouldRetry(int attempt, TimeSpan elapsed, TimeSpan nextDelay);
}

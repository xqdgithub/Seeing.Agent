namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 应用层轮次重试配置。Q1 决策：默认无限重试，仅用户取消/不可重试错误终止。
/// </summary>
public sealed class LlmTurnRetryOptions
{
    /// <summary>是否启用（默认启用）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>退避基数（毫秒），默认 500。</summary>
    public int BaseDelayMs { get; set; } = 500;

    /// <summary>单次退避上限（毫秒），默认 10000。</summary>
    public int MaxDelayMs { get; set; } = 10_000;

    /// <summary>最大尝试次数（含首次）；0 = 不限（默认）。</summary>
    public int MaxAttempts { get; set; }

    /// <summary>累计等待预算（毫秒）；0 = 不限（默认）。</summary>
    public int TotalBudgetMs { get; set; }

    public static LlmTurnRetryOptions Default => new();
}

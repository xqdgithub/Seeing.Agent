namespace Seeing.Agent.TokenBudget.Configuration;

/// <summary>
/// Token 预算配置选项（JSON: SeeingAgent:TokenBudget）
/// </summary>
public class TokenBudgetOptions
{
    public const string SectionName = "TokenBudget";

    /// <summary>
    /// 用户配置的会话上下文最大大小（可选）
    /// 设置后会与模型 context limit 取较小值
    /// </summary>
    public int? MaxContextTokens { get; set; }

    /// <summary>
    /// 无模型时的默认上下文大小
    /// 默认 200000 (200K)
    /// </summary>
    public int DefaultMaxContextTokens { get; set; } = 200000;

    /// <summary>警告阈值</summary>
    public ThresholdOptions WarningThreshold { get; set; } = new() { Percentage = 80 };

    /// <summary>压缩阈值</summary>
    public ThresholdOptions CompactionThreshold { get; set; } = new() { Percentage = 90 };

    /// <summary>滑动窗口保留 Token 数</summary>
    public int SlidingWindowKeepTokens { get; set; } = 20000;

    /// <summary>摘要目标 Token 数</summary>
    public int SummaryTargetTokens { get; set; } = 4000;

    /// <summary>是否启用自动压缩</summary>
    public bool AutoCompactionEnabled { get; set; } = true;
}

/// <summary>
/// 阈值配置选项
/// </summary>
public class ThresholdOptions
{
    /// <summary>
    /// Threshold as percentage of max tokens (0-100).
    /// Takes precedence over AbsoluteTokens if both are set.
    /// </summary>
    public int? Percentage { get; set; }

    /// <summary>
    /// Threshold as absolute token count.
    /// Ignored if Percentage is also set.
    /// </summary>
    public int? AbsoluteTokens { get; set; }
}

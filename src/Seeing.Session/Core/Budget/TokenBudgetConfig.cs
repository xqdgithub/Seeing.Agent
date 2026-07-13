namespace Seeing.Session.Core;

/// <summary>
/// Configuration for token budget management.
/// </summary>
public class TokenBudgetConfig
{
    /// <summary>
    /// Maximum context tokens.
    /// When null, uses the model's default context window.
    /// </summary>
    public int? MaxContextTokens { get; set; }

    /// <summary>
    /// Threshold for warning level.
    /// Default: 80% of max tokens.
    /// </summary>
    public ThresholdConfig WarningThreshold { get; set; } = new() { Percentage = 80 };

    /// <summary>
    /// Threshold for triggering compaction.
    /// Default: 90% of max tokens.
    /// </summary>
    public ThresholdConfig CompactionThreshold { get; set; } = new() { Percentage = 90 };

    /// <summary>
    /// Strategy to use for compaction.
    /// Default: SlidingWindow.
    /// </summary>
    public CompactionStrategyType CompactionStrategy { get; set; } = CompactionStrategyType.SlidingWindow;

    /// <summary>
    /// Number of tokens to keep when using sliding window compaction.
    /// Default: 20000.
    /// </summary>
    public int SlidingWindowKeepTokens { get; set; } = 20000;

    /// <summary>
    /// Target token count for summaries when using summarizing compaction.
    /// Default: 4000.
    /// </summary>
    public int SummaryTargetTokens { get; set; } = 4000;

    /// <summary>
    /// Whether automatic compaction is enabled.
    /// Default: true.
    /// </summary>
    public bool AutoCompactionEnabled { get; set; } = true;
}

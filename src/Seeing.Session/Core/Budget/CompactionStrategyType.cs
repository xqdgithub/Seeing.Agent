namespace Seeing.Session.Core;

/// <summary>
/// Defines the strategy type for context compaction.
/// </summary>
public enum CompactionStrategyType
{
    /// <summary>
    /// Sliding window compaction - keeps the most recent messages.
    /// </summary>
    SlidingWindow,

    /// <summary>
    /// Summarizing compaction - creates a summary of older messages.
    /// </summary>
    Summarizing,

    /// <summary>
    /// Hybrid compaction - combines sliding window with summarization.
    /// </summary>
    Hybrid
}

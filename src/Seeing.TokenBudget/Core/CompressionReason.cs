namespace Seeing.TokenBudget;

/// <summary>
/// Represents the reason for triggering compression.
/// </summary>
public enum CompressionReason
{
    /// <summary>
    /// No compression needed.
    /// </summary>
    None,

    /// <summary>
    /// Approaching the token limit - warning only, no compression yet.
    /// </summary>
    ApproachingLimit,

    /// <summary>
    /// Over threshold - compression should be triggered (deferred).
    /// </summary>
    OverThreshold,

    /// <summary>
    /// Over max limit - compression must be triggered immediately.
    /// </summary>
    OverMaxLimit
}
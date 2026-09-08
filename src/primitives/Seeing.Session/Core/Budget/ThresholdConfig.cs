namespace Seeing.Session.Core;

/// <summary>
/// Configuration for a token budget threshold.
/// Supports percentage-based or absolute token count thresholds.
/// </summary>
public class ThresholdConfig
{
    /// <summary>
    /// Percentage threshold (0-100).
    /// Takes precedence over AbsoluteTokens when both are specified.
    /// </summary>
    public int? Percentage { get; set; }

    /// <summary>
    /// Absolute token count threshold.
    /// Used when Percentage is not specified.
    /// </summary>
    public int? AbsoluteTokens { get; set; }

    /// <summary>
    /// Calculates the actual threshold value based on max tokens.
    /// </summary>
    /// <param name="maxTokens">The maximum token count.</param>
    /// <returns>The calculated threshold value.</returns>
    public int CalculateThreshold(int maxTokens)
    {
        // Percentage takes precedence
        if (Percentage.HasValue)
        {
            return (int)(maxTokens * (Percentage.Value / 100.0));
        }

        // Fall back to absolute tokens
        if (AbsoluteTokens.HasValue)
        {
            return AbsoluteTokens.Value;
        }

        // No threshold configured, return maxTokens
        return maxTokens;
    }
}

using Seeing.TokenEstimation;

namespace Seeing.TokenBudget;

/// <summary>
/// Represents the current status of token budget usage.
/// </summary>
public class BudgetStatus
{
    /// <summary>
    /// Gets or sets the current number of tokens used.
    /// </summary>
    public int CurrentTokens { get; set; }

    /// <summary>
    /// Gets or sets the maximum allowed tokens.
    /// </summary>
    public int MaxTokens { get; set; }

    /// <summary>
    /// Gets or sets the budget level based on current usage.
    /// </summary>
    public BudgetLevel Level { get; set; }

    /// <summary>
    /// Gets or sets the optional breakdown of token usage.
    /// </summary>
    public TokenBreakdown? Breakdown { get; set; }

    /// <summary>
    /// Gets the usage percentage. Returns 0 if MaxTokens is 0.
    /// </summary>
    public double UsagePercentage => MaxTokens == 0 ? 0 : (CurrentTokens / (double)MaxTokens) * 100;

    /// <summary>
    /// Gets the available tokens remaining, capped at 0.
    /// </summary>
    public int AvailableTokens => Math.Max(0, MaxTokens - CurrentTokens);
}
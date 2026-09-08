namespace Seeing.Agent.TokenBudget.Api.Responses;

/// <summary>
/// Response model for budget status information.
/// </summary>
public class BudgetStatusResponse
{
    /// <summary>
    /// Gets or sets the session identifier.
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the current token count.
    /// </summary>
    public int CurrentTokens { get; set; }

    /// <summary>
    /// Gets or sets the maximum allowed tokens.
    /// </summary>
    public int MaxTokens { get; set; }

    /// <summary>
    /// Gets or sets the usage percentage (0-100).
    /// </summary>
    public double UsagePercentage { get; set; }

    /// <summary>
    /// Gets or sets the available tokens remaining.
    /// </summary>
    public int AvailableTokens { get; set; }

    /// <summary>
    /// Gets or sets the budget level as a string.
    /// Values: "normal", "warning", "critical", "overflow".
    /// </summary>
    public string Level { get; set; } = "normal";

    /// <summary>
    /// Gets or sets whether compaction is needed.
    /// </summary>
    public bool NeedsCompaction { get; set; }

    /// <summary>
    /// Gets or sets an optional message with additional context.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets the detailed token breakdown, if available.
    /// </summary>
    public TokenBreakdownResponse? Breakdown { get; set; }
}

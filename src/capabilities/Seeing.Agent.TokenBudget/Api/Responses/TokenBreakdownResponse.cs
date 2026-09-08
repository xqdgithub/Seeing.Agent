namespace Seeing.Agent.TokenBudget.Api.Responses;

/// <summary>
/// Response model for detailed token breakdown.
/// </summary>
public class TokenBreakdownResponse
{
    /// <summary>
    /// Gets or sets the session identifier.
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the token breakdown by source.
    /// </summary>
    public SourceBreakdownData BySource { get; set; } = new();

    /// <summary>
    /// Gets or sets the token breakdown by role.
    /// </summary>
    public RoleBreakdownData ByRole { get; set; } = new();

    /// <summary>
    /// Gets or sets the total token count.
    /// </summary>
    public int TotalTokens { get; set; }

    /// <summary>
    /// Gets or sets when this breakdown was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}

namespace Seeing.TokenEstimation;

/// <summary>
/// Represents a comprehensive breakdown of token usage in a conversation.
/// </summary>
public class TokenBreakdown
{
    /// <summary>
    /// Gets or sets the breakdown of tokens by source.
    /// </summary>
    public SourceBreakdown BySource { get; set; } = new();

    /// <summary>
    /// Gets or sets the breakdown of tokens by role.
    /// </summary>
    public RoleBreakdown ByRole { get; set; } = new();

    /// <summary>
    /// Gets the total tokens from the source breakdown.
    /// </summary>
    public int Total => BySource.Total;
}

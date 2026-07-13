namespace Seeing.TokenBudget.Api.Responses;

/// <summary>
/// Represents token usage information for a single category.
/// </summary>
public class CategoryInfo
{
    /// <summary>
    /// Gets or sets the number of tokens in this category.
    /// </summary>
    public int Tokens { get; set; }

    /// <summary>
    /// Gets or sets the percentage of total tokens this category represents.
    /// </summary>
    public double Percentage { get; set; }

    /// <summary>
    /// Gets or sets the number of messages in this category.
    /// </summary>
    public int MessageCount { get; set; }
}

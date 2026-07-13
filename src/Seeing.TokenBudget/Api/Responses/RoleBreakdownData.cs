namespace Seeing.TokenBudget.Api.Responses;

/// <summary>
/// Represents token breakdown by message role.
/// </summary>
public class RoleBreakdownData
{
    /// <summary>
    /// Gets or sets the system role token information.
    /// </summary>
    public CategoryInfo System { get; set; } = new();

    /// <summary>
    /// Gets or sets the user role token information.
    /// </summary>
    public CategoryInfo User { get; set; } = new();

    /// <summary>
    /// Gets or sets the assistant role token information.
    /// </summary>
    public CategoryInfo Assistant { get; set; } = new();

    /// <summary>
    /// Gets or sets the tool role token information.
    /// </summary>
    public CategoryInfo Tool { get; set; } = new();
}

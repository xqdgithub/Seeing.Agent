namespace Seeing.TokenBudget.Api.Responses;

/// <summary>
/// Represents token breakdown by message source.
/// </summary>
public class SourceBreakdownData
{
    /// <summary>
    /// Gets or sets the system prompt token information.
    /// </summary>
    public CategoryInfo SystemPrompt { get; set; } = new();

    /// <summary>
    /// Gets or sets the tool definitions token information.
    /// </summary>
    public CategoryInfo ToolDefinitions { get; set; } = new();

    /// <summary>
    /// Gets or sets the user messages token information.
    /// </summary>
    public CategoryInfo UserMessages { get; set; } = new();

    /// <summary>
    /// Gets or sets the assistant messages token information.
    /// </summary>
    public CategoryInfo AssistantMessages { get; set; } = new();

    /// <summary>
    /// Gets or sets the tool results token information.
    /// </summary>
    public CategoryInfo ToolResults { get; set; } = new();
}

namespace Seeing.TokenEstimation;

/// <summary>
/// Represents a breakdown of tokens by their source in the conversation.
/// </summary>
public class SourceBreakdown
{
    /// <summary>
    /// Tokens from the agent system prompt.
    /// </summary>
    public int SystemPrompt { get; set; }

    /// <summary>
    /// Tokens from MCP tool definitions.
    /// </summary>
    public int ToolDefinitions { get; set; }

    /// <summary>
    /// Tokens from user messages.
    /// </summary>
    public int UserMessages { get; set; }

    /// <summary>
    /// Tokens from assistant response messages.
    /// </summary>
    public int AssistantMessages { get; set; }

    /// <summary>
    /// Tokens from tool call results.
    /// </summary>
    public int ToolResults { get; set; }

    /// <summary>
    /// Gets the total tokens across all sources.
    /// </summary>
    public int Total => SystemPrompt + ToolDefinitions + UserMessages + AssistantMessages + ToolResults;

    /// <summary>
    /// Converts the breakdown to a dictionary with standardized keys.
    /// </summary>
    /// <returns>A dictionary with keys: "system-prompt", "tool-definitions", "user-messages", "assistant-messages", "tool-results"</returns>
    public Dictionary<string, int> ToDictionary()
    {
        return new Dictionary<string, int>
        {
            ["system-prompt"] = SystemPrompt,
            ["tool-definitions"] = ToolDefinitions,
            ["user-messages"] = UserMessages,
            ["assistant-messages"] = AssistantMessages,
            ["tool-results"] = ToolResults
        };
    }
}

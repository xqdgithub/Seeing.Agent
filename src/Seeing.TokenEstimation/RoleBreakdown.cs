namespace Seeing.TokenEstimation;

/// <summary>
/// Represents a breakdown of tokens by message role.
/// </summary>
public class RoleBreakdown
{
    /// <summary>
    /// Tokens from system role messages.
    /// </summary>
    public int System { get; set; }

    /// <summary>
    /// Tokens from user role messages.
    /// </summary>
    public int User { get; set; }

    /// <summary>
    /// Tokens from assistant role messages.
    /// </summary>
    public int Assistant { get; set; }

    /// <summary>
    /// Tokens from tool role messages.
    /// </summary>
    public int Tool { get; set; }

    /// <summary>
    /// Gets the total tokens across all roles.
    /// </summary>
    public int Total => System + User + Assistant + Tool;

    /// <summary>
    /// Converts the breakdown to a dictionary with standardized keys.
    /// </summary>
    /// <returns>A dictionary with keys: "system", "user", "assistant", "tool"</returns>
    public Dictionary<string, int> ToDictionary()
    {
        return new Dictionary<string, int>
        {
            ["system"] = System,
            ["user"] = User,
            ["assistant"] = Assistant,
            ["tool"] = Tool
        };
    }
}

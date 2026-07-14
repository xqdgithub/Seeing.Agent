namespace Seeing.Agent.TokenBudget;

/// <summary>
/// Represents the result of a compression operation.
/// </summary>
public class CompressionResult
{
    /// <summary>
    /// Gets or sets whether the compression was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the token count before compression.
    /// </summary>
    public int TokensBefore { get; set; }

    /// <summary>
    /// Gets or sets the token count after compression.
    /// </summary>
    public int TokensAfter { get; set; }

    /// <summary>
    /// Gets the number of tokens saved by compression.
    /// Computed as: TokensBefore - TokensAfter
    /// </summary>
    public int TokensSaved => TokensBefore - TokensAfter;

    /// <summary>
    /// Gets or sets the number of messages removed during compression.
    /// </summary>
    public int MessagesRemoved { get; set; }

    /// <summary>
    /// Gets or sets an optional error message if compression failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Creates a successful compression result.
    /// </summary>
    public static CompressionResult Succeeded(int tokensBefore, int tokensAfter, int messagesRemoved) => new()
    {
        Success = true,
        TokensBefore = tokensBefore,
        TokensAfter = tokensAfter,
        MessagesRemoved = messagesRemoved,
        ErrorMessage = null
    };

    /// <summary>
    /// Creates a failed compression result.
    /// </summary>
    public static CompressionResult Failed(string errorMessage) => new()
    {
        Success = false,
        ErrorMessage = errorMessage
    };
}
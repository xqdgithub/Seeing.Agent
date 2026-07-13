namespace Seeing.TokenBudget.Api.Responses;

/// <summary>
/// Response model for compaction operation results.
/// </summary>
public class CompactionResponse
{
    /// <summary>
    /// Gets or sets whether the compaction was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the token count before compaction.
    /// </summary>
    public int TokensBefore { get; set; }

    /// <summary>
    /// Gets or sets the token count after compaction.
    /// </summary>
    public int TokensAfter { get; set; }

    /// <summary>
    /// Gets the number of tokens saved by compaction.
    /// </summary>
    public int TokensSaved => TokensBefore - TokensAfter;

    /// <summary>
    /// Gets or sets the number of messages removed during compaction.
    /// </summary>
    public int MessagesRemoved { get; set; }

    /// <summary>
    /// Gets or sets the strategy used for compaction.
    /// </summary>
    public string StrategyUsed { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional error message if compaction failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
}

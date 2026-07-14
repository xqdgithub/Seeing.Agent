namespace Seeing.Agent.TokenBudget;

/// <summary>
/// Represents a decision about whether compression should be triggered.
/// </summary>
public class CompressionDecision
{
    /// <summary>
    /// Gets or sets whether compression is needed.
    /// </summary>
    public bool NeedsCompression { get; set; }

    /// <summary>
    /// Gets or sets the reason for the compression decision.
    /// </summary>
    public CompressionReason Reason { get; set; }

    /// <summary>
    /// Gets or sets whether compression should be immediate.
    /// True = immediate compression, False = defer to next request.
    /// </summary>
    public bool Immediate { get; set; }

    /// <summary>
    /// Gets or sets an optional message describing the decision.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Creates a decision indicating no compression is needed.
    /// </summary>
    public static CompressionDecision NoCompression => new()
    {
        NeedsCompression = false,
        Reason = CompressionReason.None,
        Immediate = false,
        Message = null
    };
}
namespace Seeing.Session.Compression;

/// <summary>
/// Interface for text summarization services.
/// Used by compression strategies that require LLM-based summarization.
/// </summary>
public interface ISummarizer
{
    /// <summary>
    /// Generates a summary of the provided text.
    /// </summary>
    /// <param name="text">The text to summarize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The generated summary.</returns>
    Task<string> SummarizeAsync(string text, CancellationToken cancellationToken = default);
}

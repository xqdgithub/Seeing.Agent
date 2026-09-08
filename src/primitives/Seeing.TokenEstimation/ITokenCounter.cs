namespace Seeing.TokenEstimation;

/// <summary>
/// Interface for token counting/estimation strategies.
/// </summary>
public interface ITokenCounter
{
    /// <summary>
    /// Estimates the number of tokens in the given content.
    /// </summary>
    /// <param name="content">The content to estimate tokens for.</param>
    /// <returns>The estimated number of tokens.</returns>
    int Estimate(string content);
}

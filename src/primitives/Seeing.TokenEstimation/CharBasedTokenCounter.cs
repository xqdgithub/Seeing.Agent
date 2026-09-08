namespace Seeing.TokenEstimation;

/// <summary>
/// A simple character-based token counter that estimates tokens based on character count.
/// Uses 4 characters per token (similar to OpenCode's estimation).
/// </summary>
public class CharBasedTokenCounter : ITokenCounter
{
    private const int CharactersPerToken = 4;

    /// <summary>
    /// Estimates the number of tokens in the given content.
    /// Uses a simple heuristic of 4 characters per token.
    /// </summary>
    /// <param name="content">The content to estimate tokens for.</param>
    /// <returns>The estimated number of tokens, or 0 if content is null or empty.</returns>
    public int Estimate(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return 0;
        }

        return (int)Math.Round(content.Length / (double)CharactersPerToken);
    }
}
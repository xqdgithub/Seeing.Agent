using Seeing.Session.Core;
using Seeing.TokenEstimation;

namespace Seeing.Session.Compression.Strategies;

/// <summary>
/// Hybrid compression strategy that combines sliding window and summarizing strategies.
/// First tries sliding window compression, then falls back to summarizing if needed.
/// </summary>
public class HybridStrategy : ICompressionStrategy
{
    private readonly SlidingWindowTokenStrategy _slidingWindowStrategy;
    private readonly SummarizingStrategy _summarizingStrategy;

    /// <summary>
    /// Gets the strategy name.
    /// </summary>
    public string Name => "hybrid";

    /// <summary>
    /// Creates a new instance of HybridStrategy.
    /// </summary>
    /// <param name="slidingWindowStrategy">The sliding window strategy to use as first pass.</param>
    /// <param name="summarizingStrategy">The summarizing strategy to use as fallback.</param>
    public HybridStrategy(
        SlidingWindowTokenStrategy slidingWindowStrategy,
        SummarizingStrategy summarizingStrategy)
    {
        ArgumentNullException.ThrowIfNull(slidingWindowStrategy);
        ArgumentNullException.ThrowIfNull(summarizingStrategy);
        _slidingWindowStrategy = slidingWindowStrategy;
        _summarizingStrategy = summarizingStrategy;
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionMessage> Compress(IReadOnlyList<SessionMessage> messages)
    {
        // Default to sliding window compression
        return _slidingWindowStrategy.Compress(messages);
    }

    /// <inheritdoc />
    public int EstimateRetainedCount(int messageCount)
    {
        return _slidingWindowStrategy.EstimateRetainedCount(messageCount);
    }

    /// <summary>
    /// Compresses messages using a hybrid approach.
    /// First tries sliding window compression. If the result is still over the target,
    /// falls back to summarizing compression.
    /// </summary>
    /// <param name="messages">The messages to compress.</param>
    /// <param name="config">Token budget configuration.</param>
    /// <param name="tokenCounter">Token counter for estimating message sizes.</param>
    /// <returns>Compression result with the best outcome.</returns>
    public CompressionResult CompressByTokenBudget(
        IReadOnlyList<SessionMessage> messages,
        TokenBudgetConfig config,
        ITokenCounter tokenCounter)
    {
        // Handle empty messages
        if (messages.Count == 0)
        {
            return CompressionResult.Succeeded(0, 0, 0, Array.Empty<SessionMessage>());
        }

        var targetTokens = config.SlidingWindowKeepTokens;

        // Step 1: Try sliding window compression
        var slidingResult = _slidingWindowStrategy.CompressByTokenBudget(messages, config, tokenCounter);

        // If sliding window succeeded and meets target, return it
        if (slidingResult.Success && slidingResult.CompressedMessages != null)
        {
            var tokensAfter = CountTokens(slidingResult.CompressedMessages, tokenCounter);
            if (tokensAfter <= targetTokens)
            {
                return slidingResult;
            }
        }

        // Step 2: Try summarizing compression
        var summaryConfig = new TokenBudgetConfig
        {
            SummaryTargetTokens = config.SummaryTargetTokens,
            MaxContextTokens = config.MaxContextTokens
        };

        var summaryResult = _summarizingStrategy.CompressByTokenBudget(messages, summaryConfig, tokenCounter);

        // If summarizing succeeded, return it
        if (summaryResult.Success)
        {
            return summaryResult;
        }

        // Both failed, return sliding window result (even if partial success)
        return slidingResult;
    }

    /// <summary>
    /// Counts the total tokens in a collection of messages.
    /// </summary>
    /// <param name="messages">The messages to count tokens for.</param>
    /// <param name="counter">The token counter to use.</param>
    /// <returns>Total token count.</returns>
    private int CountTokens(IReadOnlyList<SessionMessage> messages, ITokenCounter counter)
    {
        if (messages == null) return 0;
        var total = 0;
        foreach (var message in messages)
        {
            total += counter.Estimate(message.Content ?? string.Empty);
            if (!string.IsNullOrEmpty(message.ReasoningContent))
                total += counter.Estimate(message.ReasoningContent);
            if (message.ToolCalls != null)
            {
                foreach (var toolCall in message.ToolCalls)
                {
                    total += counter.Estimate(toolCall.Name ?? string.Empty);
                    total += counter.Estimate(toolCall.Arguments ?? string.Empty);
                }
            }
            if (message.Parts != null)
            {
                foreach (var part in message.Parts)
                {
                    if (!string.IsNullOrEmpty(part.Text))
                        total += counter.Estimate(part.Text);
                }
            }
        }
        return total;
    }
}

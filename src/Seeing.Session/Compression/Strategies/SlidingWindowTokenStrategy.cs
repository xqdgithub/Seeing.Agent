using Seeing.Session.Core;
using Seeing.TokenEstimation;

namespace Seeing.Session.Compression.Strategies;

/// <summary>
/// Token-based sliding window compression strategy.
/// Compresses messages based on token budget constraints while preserving
/// the first message (system prompt) and recent messages.
/// </summary>
public class SlidingWindowTokenStrategy : ICompressionStrategy
{
    private readonly int _keepLastN;

    /// <summary>
    /// Gets the strategy name.
    /// </summary>
    public string Name => "sliding-window-token";

    /// <summary>
    /// Creates a new instance of SlidingWindowTokenStrategy.
    /// </summary>
    /// <param name="keepLastN">Number of recent messages to always keep (default: 20).</param>
    public SlidingWindowTokenStrategy(int keepLastN = 20)
    {
        _keepLastN = keepLastN;
    }

    /// <summary>
    /// Compresses messages using a simple count-based sliding window.
    /// </summary>
    /// <param name="messages">The messages to compress.</param>
    /// <returns>Compressed message list.</returns>
    public IReadOnlyList<SessionMessage> Compress(IReadOnlyList<SessionMessage> messages)
    {
        if (messages.Count <= _keepLastN + 1)
        {
            return messages;
        }

        var result = new List<SessionMessage>();

        // Keep first message (system prompt)
        result.Add(messages[0]);

        // Keep last N messages
        var startIndex = messages.Count - _keepLastN;
        for (var i = startIndex; i < messages.Count; i++)
        {
            result.Add(messages[i]);
        }

        return result;
    }

    /// <summary>
    /// Estimates the number of messages that will be retained after compression.
    /// </summary>
    /// <param name="messageCount">Total message count before compression.</param>
    /// <returns>Estimated retained message count.</returns>
    public int EstimateRetainedCount(int messageCount)
    {
        if (messageCount <= _keepLastN + 1)
        {
            return messageCount;
        }

        // First message + last N messages
        return 1 + _keepLastN;
    }

    /// <summary>
    /// Compresses messages based on token budget constraints.
    /// Keeps the first message (system prompt) and adds messages from the end
    /// until the target token count is reached.
    /// </summary>
    /// <param name="messages">The messages to compress.</param>
    /// <param name="config">Token budget configuration.</param>
    /// <param name="tokenCounter">Token counter for estimating message sizes.</param>
    /// <returns>Compression result with before/after token counts.</returns>
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

        // Calculate tokens before compression
        var tokensBefore = CountTokens(messages, tokenCounter);

        // Determine target token count
        var targetTokens = config.SlidingWindowKeepTokens;

        // If no max context specified, just use simple compression
        if (!config.MaxContextTokens.HasValue || tokensBefore <= targetTokens)
        {
            // No compression needed or no limit specified
            return CompressionResult.Succeeded(
                tokensBefore,
                tokensBefore,
                0,
                messages);
        }

        // Build compressed message list
        var result = new List<SessionMessage>();

        // Always keep first message (system prompt)
        var firstMessage = messages[0];
        var firstMessageTokens = CountTokens(new[] { firstMessage }, tokenCounter);
        result.Add(firstMessage);

        var currentTokens = firstMessageTokens;

        // Add messages from the end until we reach target tokens
        for (var i = messages.Count - 1; i >= 1; i--)
        {
            var messageTokens = CountTokens(new[] { messages[i] }, tokenCounter);

            // Check if adding this message would exceed target
            if (currentTokens + messageTokens <= targetTokens)
            {
                result.Insert(1, messages[i]);
                currentTokens += messageTokens;
            }
            else
            {
                // Can't fit more messages, stop
                break;
            }
        }

        var tokensAfter = CountTokens(result, tokenCounter);

        return CompressionResult.Succeeded(
            tokensBefore,
            tokensAfter,
            messages.Count - result.Count,
            result);
    }

    /// <summary>
    /// Counts the total tokens in a collection of messages.
    /// </summary>
    /// <param name="messages">The messages to count tokens for.</param>
    /// <param name="tokenCounter">The token counter to use.</param>
    /// <returns>Total token count.</returns>
    private int CountTokens(IReadOnlyList<SessionMessage> messages, ITokenCounter tokenCounter)
    {
        var total = 0;
        foreach (var message in messages)
        {
            total += tokenCounter.Estimate(message.Content);

            if (!string.IsNullOrEmpty(message.ReasoningContent))
            {
                total += tokenCounter.Estimate(message.ReasoningContent);
            }

            if (message.ToolCalls != null)
            {
                foreach (var toolCall in message.ToolCalls)
                {
                    total += tokenCounter.Estimate(toolCall.Name);
                    total += tokenCounter.Estimate(toolCall.Arguments);
                }
            }

            if (message.Parts != null)
            {
                foreach (var part in message.Parts)
                {
                    if (!string.IsNullOrEmpty(part.Text))
                    {
                        total += tokenCounter.Estimate(part.Text);
                    }
                }
            }
        }

        return total;
    }
}

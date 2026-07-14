using Seeing.Session.Core;
using Seeing.TokenEstimation;

namespace Seeing.Session.Compression.Strategies;

/// <summary>
/// LLM-based summarizing compression strategy.
/// Uses a summarizer service to generate conversation summaries for middle messages
/// while preserving the first message (system prompt) and recent messages.
/// </summary>
public class SummarizingStrategy : ICompressionStrategy
{
    private readonly ISummarizer _summarizer;
    private readonly int _keepRecentMessages;

    /// <summary>
    /// Strategy name identifier.
    /// </summary>
    public string Name => "summarizing";

    /// <summary>
    /// Summary prompt template.
    /// </summary>
    private const string SummaryPromptTemplate = @"Please summarize the following conversation history concisely, preserving key information:
- User's main requests
- Key actions completed by the assistant
- Important context (file paths, decisions, etc.)

Conversation history:
{text}

Summary format:
[User Request] ...
[Completed Actions] ...
[Important Context] ...";

    /// <summary>
    /// Creates a new instance of SummarizingStrategy.
    /// </summary>
    /// <param name="summarizer">The summarizer service for generating summaries.</param>
    /// <param name="keepRecentMessages">Number of recent messages to always keep (default: 4).</param>
    /// <exception cref="ArgumentNullException">Thrown when summarizer is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when keepRecentMessages is negative.</exception>
    public SummarizingStrategy(ISummarizer summarizer, int keepRecentMessages = 4)
    {
        ArgumentNullException.ThrowIfNull(summarizer);
        if (keepRecentMessages < 0)
            throw new ArgumentOutOfRangeException(nameof(keepRecentMessages), "Must be non-negative");
        _summarizer = summarizer;
        _keepRecentMessages = keepRecentMessages;
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionMessage> Compress(IReadOnlyList<SessionMessage> messages)
    {
        // Simple implementation: keep first and last N messages
        if (messages.Count <= _keepRecentMessages + 1)
            return messages;

        var result = new List<SessionMessage> { messages[0] };
        var startIndex = messages.Count - _keepRecentMessages;
        for (var i = startIndex; i < messages.Count; i++)
        {
            result.Add(messages[i]);
        }
        return result;
    }

    /// <inheritdoc />
    public int EstimateRetainedCount(int messageCount)
    {
        if (messageCount <= _keepRecentMessages + 1)
            return messageCount;
        return 1 + _keepRecentMessages;
    }

    /// <summary>
    /// Compresses messages using LLM summarization.
    /// </summary>
    /// <remarks>
    /// Warning: This method blocks on async summarization. 
    /// Callers should use Task.Run() if needed to avoid blocking.
    /// </remarks>
    /// <inheritdoc />
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

        var tokensBefore = CountTokens(messages, tokenCounter);
        var targetTokens = config.SummaryTargetTokens;

        // No compression needed
        if (tokensBefore <= targetTokens)
        {
            return CompressionResult.Succeeded(tokensBefore, tokensBefore, 0, messages);
        }

        // Not enough messages to compress
        if (messages.Count <= _keepRecentMessages + 1)
        {
            return CompressionResult.Succeeded(tokensBefore, tokensBefore, 0, messages);
        }

        try
        {
            // Build summary request for middle messages
            var toSummarize = messages.Skip(1).Take(messages.Count - _keepRecentMessages - 1).ToList();
            var summaryContent = GenerateSummaryAsync(toSummarize).GetAwaiter().GetResult();

            // Build compressed message list
            var result = new List<SessionMessage> { messages[0] }; // Keep system prompt

            // Add summary message
            result.Add(SessionMessage.SystemMessage($"[Conversation Summary]\n{summaryContent}"));

            // Add recent messages
            for (var i = messages.Count - _keepRecentMessages; i < messages.Count; i++)
            {
                result.Add(messages[i]);
            }

            var tokensAfter = CountTokens(result, tokenCounter);

            return CompressionResult.Succeeded(
                tokensBefore,
                tokensAfter,
                messages.Count - result.Count,
                result);
        }
        catch (Exception ex)
        {
            return CompressionResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Generates a summary for the specified messages.
    /// </summary>
    /// <param name="messages">The messages to summarize.</param>
    /// <returns>The generated summary.</returns>
    private async Task<string> GenerateSummaryAsync(IReadOnlyList<SessionMessage> messages)
    {
        var historyText = string.Join("\n\n", messages.Select(m =>
            $"[{m.Role}]: {m.Content}"));

        return await _summarizer.SummarizeAsync(historyText);
    }

    /// <summary>
    /// Counts the total tokens in a collection of messages.
    /// </summary>
    /// <param name="messages">The messages to count tokens for.</param>
    /// <param name="counter">The token counter to use.</param>
    /// <returns>Total token count.</returns>
    private int CountTokens(IReadOnlyList<SessionMessage> messages, ITokenCounter counter)
    {
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
        }
        return total;
    }
}

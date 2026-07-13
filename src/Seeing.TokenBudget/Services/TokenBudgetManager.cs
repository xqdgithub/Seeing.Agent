using Seeing.Session.Core;
using Seeing.TokenEstimation;

namespace Seeing.TokenBudget;

/// <summary>
/// Default implementation of token budget management.
/// Calculates token breakdowns and determines budget levels based on configuration.
/// </summary>
public class TokenBudgetManager : ITokenBudgetManager
{
    private const int DefaultMaxContextTokens = 128000; // Default for most modern models
    
    private readonly ITokenCounter _tokenCounter;

    /// <summary>
    /// Creates a new TokenBudgetManager with an optional custom token counter.
    /// </summary>
    /// <param name="tokenCounter">Optional token counter implementation. Defaults to CharBasedTokenCounter.</param>
    public TokenBudgetManager(ITokenCounter? tokenCounter = null)
    {
        _tokenCounter = tokenCounter ?? new CharBasedTokenCounter();
    }

    /// <inheritdoc />
    public TokenBreakdown CalculateBreakdown(SessionData session, string? systemPrompt = null, int? toolTokens = null)
    {
        var breakdown = new TokenBreakdown();

        // Count system prompt tokens if provided
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            breakdown.BySource.SystemPrompt = _tokenCounter.Estimate(systemPrompt);
            breakdown.ByRole.System += breakdown.BySource.SystemPrompt;
        }

        // Add tool tokens if provided
        if (toolTokens.HasValue)
        {
            breakdown.BySource.ToolDefinitions = toolTokens.Value;
        }

        // Process session messages
        foreach (var message in session.Messages)
        {
            var messageTokens = EstimateMessageTokens(message);

            // Distribute to BySource based on Role
            switch (message.Role)
            {
                case MessageRole.System:
                    breakdown.BySource.SystemPrompt += messageTokens;
                    breakdown.ByRole.System += messageTokens;
                    break;
                case MessageRole.User:
                    breakdown.BySource.UserMessages += messageTokens;
                    breakdown.ByRole.User += messageTokens;
                    break;
                case MessageRole.Assistant:
                    breakdown.BySource.AssistantMessages += messageTokens;
                    breakdown.ByRole.Assistant += messageTokens;
                    break;
                case MessageRole.Tool:
                    breakdown.BySource.ToolResults += messageTokens;
                    breakdown.ByRole.Tool += messageTokens;
                    break;
            }
        }

        return breakdown;
    }

    /// <inheritdoc />
    public BudgetStatus CheckBudget(SessionData session, TokenBudgetConfig config, int currentTokens)
    {
        var maxTokens = config.MaxContextTokens ?? DefaultMaxContextTokens;
        var level = DetermineLevel(currentTokens, maxTokens, config);
        var breakdown = CalculateBreakdown(session);

        return new BudgetStatus
        {
            CurrentTokens = currentTokens,
            MaxTokens = maxTokens,
            Level = level,
            Breakdown = breakdown
        };
    }

    /// <inheritdoc />
    public BudgetLevel DetermineLevel(int currentTokens, int maxTokens, TokenBudgetConfig config)
    {
        // Handle edge cases
        if (maxTokens <= 0)
        {
            return BudgetLevel.Normal;
        }

        // Check overflow first
        if (currentTokens > maxTokens)
        {
            return BudgetLevel.Overflow;
        }

        // Calculate thresholds
        var warningThreshold = config.WarningThreshold.CalculateThreshold(maxTokens);
        var criticalThreshold = config.CompactionThreshold.CalculateThreshold(maxTokens);

        // Determine level based on thresholds
        if (currentTokens >= criticalThreshold)
        {
            return BudgetLevel.Critical;
        }

        if (currentTokens >= warningThreshold)
        {
            return BudgetLevel.Warning;
        }

        return BudgetLevel.Normal;
    }

    /// <summary>
    /// Estimates the total tokens for a message, including content and reasoning content.
    /// </summary>
    private int EstimateMessageTokens(SessionMessage message)
    {
        var total = 0;

        // Count main content
        if (!string.IsNullOrEmpty(message.Content))
        {
            total += _tokenCounter.Estimate(message.Content);
        }

        // Count reasoning content (for thinking models like DeepSeek-R1)
        if (!string.IsNullOrEmpty(message.ReasoningContent))
        {
            total += _tokenCounter.Estimate(message.ReasoningContent);
        }

        // Note: We don't count Parts (images, files) as they have different token costs
        // depending on the model provider. Those should be handled separately.

        return total;
    }
}
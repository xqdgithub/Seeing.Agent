using Seeing.Session.Core;
using Seeing.TokenEstimation;

namespace Seeing.TokenBudget;

/// <summary>
/// Interface for managing token budget calculations and status checks.
/// </summary>
public interface ITokenBudgetManager
{
    /// <summary>
    /// Calculates a comprehensive breakdown of token usage for a session.
    /// </summary>
    /// <param name="session">The session to analyze.</param>
    /// <param name="systemPrompt">Optional system prompt to include in the breakdown.</param>
    /// <param name="toolTokens">Optional tool definition token count to include.</param>
    /// <returns>A TokenBreakdown with detailed distribution by source and role.</returns>
    TokenBreakdown CalculateBreakdown(SessionData session, string? systemPrompt = null, int? toolTokens = null);

    /// <summary>
    /// Checks the budget status for a session.
    /// </summary>
    /// <param name="session">The session to check.</param>
    /// <param name="config">The budget configuration.</param>
    /// <param name="currentTokens">The current token count.</param>
    /// <returns>A BudgetStatus with current usage and level information.</returns>
    BudgetStatus CheckBudget(SessionData session, TokenBudgetConfig config, int currentTokens);

    /// <summary>
    /// Determines the budget level based on current token usage.
    /// </summary>
    /// <param name="currentTokens">The current token count.</param>
    /// <param name="maxTokens">The maximum allowed tokens.</param>
    /// <param name="config">The budget configuration with threshold settings.</param>
    /// <returns>The appropriate BudgetLevel.</returns>
    BudgetLevel DetermineLevel(int currentTokens, int maxTokens, TokenBudgetConfig config);
}

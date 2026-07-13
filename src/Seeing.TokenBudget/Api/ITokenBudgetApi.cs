using Seeing.Session.Core;
using Seeing.TokenBudget.Api.Responses;

namespace Seeing.TokenBudget.Api;

/// <summary>
/// API interface for token budget management operations.
/// </summary>
public interface ITokenBudgetApi
{
    /// <summary>
    /// Gets a detailed token breakdown for a session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <returns>A detailed breakdown of token usage by source and role.</returns>
    Task<TokenBreakdownResponse> GetBreakdownAsync(string sessionId);

    /// <summary>
    /// Gets the current budget status for a session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <returns>The current budget status including usage and level.</returns>
    Task<BudgetStatusResponse> GetBudgetStatusAsync(string sessionId);

    /// <summary>
    /// Triggers compaction for a session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="strategy">Optional compaction strategy. If null, uses the configured strategy.</param>
    /// <returns>The result of the compaction operation.</returns>
    Task<CompactionResponse> TriggerCompactionAsync(string sessionId, CompactionStrategyType? strategy = null);

    /// <summary>
    /// Updates the token budget configuration for a session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="config">The new budget configuration.</param>
    Task UpdateConfigAsync(string sessionId, TokenBudgetConfig config);

    /// <summary>
    /// Gets the effective token budget configuration for a session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <returns>The effective (resolved) token budget configuration.</returns>
    Task<TokenBudgetConfig> GetEffectiveConfigAsync(string sessionId);
}

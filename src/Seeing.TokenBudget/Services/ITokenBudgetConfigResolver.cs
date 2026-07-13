using Seeing.Session.Core;

namespace Seeing.TokenBudget;

/// <summary>
/// Resolves token budget configuration with multi-level priority.
/// Priority order: Session > Agent > Global
/// </summary>
public interface ITokenBudgetConfigResolver
{
    /// <summary>
    /// Resolves the final configuration by merging configs at different levels.
    /// </summary>
    /// <param name="sessionConfig">Session-level config (highest priority)</param>
    /// <param name="agentConfig">Agent-level config (medium priority)</param>
    /// <param name="globalConfig">Global-level config (lowest priority)</param>
    /// <returns>The merged configuration</returns>
    TokenBudgetConfig Resolve(
        TokenBudgetConfig? sessionConfig, 
        TokenBudgetConfig? agentConfig, 
        TokenBudgetConfig? globalConfig);
}

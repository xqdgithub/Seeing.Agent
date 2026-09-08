using Seeing.Session.Core;

namespace Seeing.Agent.TokenBudget;

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

    /// <summary>
    /// Resolves token budget configuration with model limits as upper bounds.
    /// Model limits constrain the maximum context tokens but respect user's smaller values.
    /// </summary>
    /// <param name="sessionConfig">Session-level config (highest priority)</param>
    /// <param name="agentConfig">Agent-level config (medium priority)</param>
    /// <param name="globalConfig">Global-level config (lowest priority)</param>
    /// <param name="modelContextLimit">Model's intrinsic context limit (upper bound constraint)</param>
    /// <returns>The resolved configuration with model limits applied</returns>
    TokenBudgetConfig ResolveWithModelLimits(
        TokenBudgetConfig? sessionConfig,
        TokenBudgetConfig? agentConfig,
        TokenBudgetConfig? globalConfig,
        int? modelContextLimit);
}

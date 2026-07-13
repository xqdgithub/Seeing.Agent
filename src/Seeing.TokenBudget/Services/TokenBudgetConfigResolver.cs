using Seeing.Session.Core;

namespace Seeing.TokenBudget;

/// <summary>
/// Resolves token budget configuration with multi-level priority.
/// Priority order: Session > Agent > Global
/// </summary>
public class TokenBudgetConfigResolver : ITokenBudgetConfigResolver
{
    // Default values for comparison
    private const CompactionStrategyType DefaultCompactionStrategy = CompactionStrategyType.SlidingWindow;
    private const int DefaultSlidingWindowKeepTokens = 20000;
    private const int DefaultSummaryTargetTokens = 4000;
    private const bool DefaultAutoCompactionEnabled = true;
    private const int DefaultWarningThresholdPercentage = 80;
    private const int DefaultCompactionThresholdPercentage = 90;
    
    /// <inheritdoc />
    public TokenBudgetConfig Resolve(
        TokenBudgetConfig? sessionConfig, 
        TokenBudgetConfig? agentConfig, 
        TokenBudgetConfig? globalConfig)
    {
        // Start with global config (lowest priority) or default
        var result = new TokenBudgetConfig();
        
        if (globalConfig is not null)
        {
            result = MergeConfigs(result, globalConfig);
        }
        
        // Merge agent config (medium priority)
        if (agentConfig is not null)
        {
            result = MergeConfigs(result, agentConfig);
        }
        
        // Merge session config (highest priority)
        if (sessionConfig is not null)
        {
            result = MergeConfigs(result, sessionConfig);
        }
        
        return result;
    }
    
    /// <summary>
    /// Merges the override config on top of the base config.
    /// For nullable values (MaxContextTokens): override ?? base
    /// For ThresholdConfig: merge individual nullable properties
    /// For non-nullable value types: use override if different from default, else use base
    /// </summary>
    private TokenBudgetConfig MergeConfigs(TokenBudgetConfig baseConfig, TokenBudgetConfig overrideConfig)
    {
        var result = new TokenBudgetConfig
        {
            // Nullable values: use override if set, otherwise use base
            MaxContextTokens = overrideConfig.MaxContextTokens ?? baseConfig.MaxContextTokens,
            
            // Thresholds: merge with special handling for Percentage and AbsoluteTokens
            WarningThreshold = MergeThresholds(baseConfig.WarningThreshold, overrideConfig.WarningThreshold, isWarningThreshold: true),
            CompactionThreshold = MergeThresholds(baseConfig.CompactionThreshold, overrideConfig.CompactionThreshold, isWarningThreshold: false),
            
            // Non-nullable value types: use override if different from default, else inherit from base
            CompactionStrategy = GetNonNullableValue(baseConfig.CompactionStrategy, overrideConfig.CompactionStrategy, DefaultCompactionStrategy),
            SlidingWindowKeepTokens = GetNonNullableValue(baseConfig.SlidingWindowKeepTokens, overrideConfig.SlidingWindowKeepTokens, DefaultSlidingWindowKeepTokens),
            SummaryTargetTokens = GetNonNullableValue(baseConfig.SummaryTargetTokens, overrideConfig.SummaryTargetTokens, DefaultSummaryTargetTokens),
            AutoCompactionEnabled = GetNonNullableValue(baseConfig.AutoCompactionEnabled, overrideConfig.AutoCompactionEnabled, DefaultAutoCompactionEnabled)
        };
        
        return result;
    }
    
    /// <summary>
    /// Gets the merged value for a non-nullable property.
    /// If override differs from default, use override; otherwise use base.
    /// </summary>
    private static T GetNonNullableValue<T>(T baseValue, T overrideValue, T defaultValue)
    {
        // If override is different from default, it was explicitly set
        if (!EqualityComparer<T>.Default.Equals(overrideValue, defaultValue))
        {
            return overrideValue;
        }
        // Otherwise inherit from base
        return baseValue;
    }
    
    /// <summary>
    /// Merges threshold configurations.
    /// For Percentage: if override is null or equals default percentage, use base
    /// For AbsoluteTokens: if override is null, use base
    /// </summary>
    private ThresholdConfig MergeThresholds(ThresholdConfig baseThreshold, ThresholdConfig overrideThreshold, bool isWarningThreshold)
    {
        var defaultPercentage = isWarningThreshold ? DefaultWarningThresholdPercentage : DefaultCompactionThresholdPercentage;
        
        return new ThresholdConfig
        {
            // If override has Percentage explicitly set (not null and not default), use it; otherwise inherit from base
            Percentage = MergeThresholdPercentage(baseThreshold.Percentage, overrideThreshold.Percentage, defaultPercentage),
            // If override has AbsoluteTokens explicitly set (not null), use it; otherwise inherit from base
            AbsoluteTokens = overrideThreshold.AbsoluteTokens ?? baseThreshold.AbsoluteTokens
        };
    }
    
    /// <summary>
    /// Merges threshold percentage values.
    /// Uses override if it's explicitly set (not null and different from default).
    /// </summary>
    private static int? MergeThresholdPercentage(int? basePercentage, int? overridePercentage, int defaultPercentage)
    {
        // If override is null, use base
        if (!overridePercentage.HasValue)
        {
            return basePercentage;
        }
        
        // If override equals the default, treat as "not set" and use base
        if (overridePercentage.Value == defaultPercentage)
        {
            return basePercentage;
        }
        
        // Otherwise use the explicitly set override value
        return overridePercentage;
    }
}

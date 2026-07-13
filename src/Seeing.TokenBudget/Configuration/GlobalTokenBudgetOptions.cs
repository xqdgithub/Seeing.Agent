using Seeing.Session.Core;

namespace Seeing.TokenBudget.Configuration;

/// <summary>
/// Global configuration options for token budget management.
/// </summary>
public class GlobalTokenBudgetOptions
{
    /// <summary>
    /// The configuration section name for token budget settings.
    /// </summary>
    public const string SectionName = "TokenBudget";
    
    /// <summary>
    /// Gets or sets the default token budget configuration.
    /// </summary>
    public TokenBudgetConfig DefaultConfig { get; set; } = new()
    {
        MaxContextTokens = 128000,
        WarningThreshold = new ThresholdConfig { Percentage = 80 },
        CompactionThreshold = new ThresholdConfig { Percentage = 90 },
        CompactionStrategy = CompactionStrategyType.Hybrid,
        SlidingWindowKeepTokens = 20000,
        SummaryTargetTokens = 4000,
        AutoCompactionEnabled = true
    };
}

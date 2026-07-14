using Seeing.Session.Compression;
using Seeing.Session.Compression.Strategies;
using Seeing.Session.Core;

namespace Seeing.TokenBudget;

/// <summary>
/// 压缩策略工厂实现
/// </summary>
public class CompressionStrategyFactory : ICompressionStrategyFactory
{
    private readonly SlidingWindowTokenStrategy _slidingWindowStrategy;
    private readonly SummarizingStrategy _summarizingStrategy;
    private readonly HybridStrategy _hybridStrategy;

    public CompressionStrategyFactory(
        SlidingWindowTokenStrategy slidingWindowStrategy,
        SummarizingStrategy summarizingStrategy,
        HybridStrategy hybridStrategy)
    {
        _slidingWindowStrategy = slidingWindowStrategy;
        _summarizingStrategy = summarizingStrategy;
        _hybridStrategy = hybridStrategy;
    }

    public ICompressionStrategy GetStrategy(CompactionStrategyType type)
    {
        return type switch
        {
            CompactionStrategyType.SlidingWindow => _slidingWindowStrategy,
            CompactionStrategyType.Summarizing => _summarizingStrategy,
            CompactionStrategyType.Hybrid => _hybridStrategy,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown compression strategy type")
        };
    }
}

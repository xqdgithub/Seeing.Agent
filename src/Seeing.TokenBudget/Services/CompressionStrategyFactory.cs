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
    private readonly SummarizingStrategy? _summarizingStrategy;
    private readonly HybridStrategy? _hybridStrategy;

    public CompressionStrategyFactory(
        SlidingWindowTokenStrategy slidingWindowStrategy,
        SummarizingStrategy? summarizingStrategy = null,
        HybridStrategy? hybridStrategy = null)
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
            CompactionStrategyType.Summarizing => _summarizingStrategy 
                ?? throw new InvalidOperationException("SummarizingStrategy not registered. Register ISummarizer to enable LLM-based compression."),
            CompactionStrategyType.Hybrid => _hybridStrategy 
                ?? throw new InvalidOperationException("HybridStrategy not registered. Register ISummarizer to enable hybrid compression."),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown compression strategy type")
        };
    }
}

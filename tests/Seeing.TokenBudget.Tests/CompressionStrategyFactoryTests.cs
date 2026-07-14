using FluentAssertions;
using Moq;
using Seeing.Session.Compression;
using Seeing.Session.Compression.Strategies;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.TokenBudget.Tests;

public class CompressionStrategyFactoryTests
{
    private readonly Mock<ISummarizer> _summarizerMock = new();

    [Fact]
    public void GetStrategy_SlidingWindow_ReturnsSlidingWindowTokenStrategy()
    {
        // Arrange
        var factory = CreateFactory();

        // Act
        var strategy = factory.GetStrategy(CompactionStrategyType.SlidingWindow);

        // Assert
        strategy.Should().BeOfType<SlidingWindowTokenStrategy>();
    }

    [Fact]
    public void GetStrategy_Summarizing_ReturnsSummarizingStrategy()
    {
        // Arrange
        var factory = CreateFactory();

        // Act
        var strategy = factory.GetStrategy(CompactionStrategyType.Summarizing);

        // Assert
        strategy.Should().BeOfType<SummarizingStrategy>();
    }

    [Fact]
    public void GetStrategy_Hybrid_ReturnsHybridStrategy()
    {
        // Arrange
        var factory = CreateFactory();

        // Act
        var strategy = factory.GetStrategy(CompactionStrategyType.Hybrid);

        // Assert
        strategy.Should().BeOfType<HybridStrategy>();
    }

    [Fact]
    public void GetStrategy_InvalidType_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var factory = CreateFactory();
        var invalidType = (CompactionStrategyType)999;

        // Act
        var act = () => factory.GetStrategy(invalidType);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("type");
    }

    [Fact]
    public void GetStrategy_ReturnsSameInstance_ForSameType()
    {
        // Arrange
        var factory = CreateFactory();

        // Act
        var strategy1 = factory.GetStrategy(CompactionStrategyType.SlidingWindow);
        var strategy2 = factory.GetStrategy(CompactionStrategyType.SlidingWindow);

        // Assert
        strategy1.Should().BeSameAs(strategy2);
    }

    [Fact]
    public void GetStrategy_ReturnsCorrectStrategyNames()
    {
        // Arrange
        var factory = CreateFactory();

        // Act & Assert
        var slidingStrategy = factory.GetStrategy(CompactionStrategyType.SlidingWindow);
        slidingStrategy.Name.Should().Be("sliding-window-token");

        var summarizingStrategy = factory.GetStrategy(CompactionStrategyType.Summarizing);
        summarizingStrategy.Name.Should().Be("summarizing");

        var hybridStrategy = factory.GetStrategy(CompactionStrategyType.Hybrid);
        hybridStrategy.Name.Should().Be("hybrid");
    }

    private CompressionStrategyFactory CreateFactory()
    {
        var slidingWindow = new SlidingWindowTokenStrategy();
        var summarizing = new SummarizingStrategy(_summarizerMock.Object);
        var hybrid = new HybridStrategy(slidingWindow, summarizing);

        return new CompressionStrategyFactory(slidingWindow, summarizing, hybrid);
    }
}

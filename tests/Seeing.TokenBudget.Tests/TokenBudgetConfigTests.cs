using FluentAssertions;
using Seeing.Session.Core;
using Seeing.TokenBudget;
using Xunit;

namespace Seeing.TokenBudget.Tests;

public class TokenBudgetConfigTests
{
    [Fact]
    public void ThresholdConfig_CalculateThreshold_WithPercentage_ReturnsCorrectValue()
    {
        // Arrange
        var config = new ThresholdConfig { Percentage = 80 };

        // Act
        var result = config.CalculateThreshold(100000);

        // Assert
        result.Should().Be(80000);
    }

    [Fact]
    public void ThresholdConfig_CalculateThreshold_WithAbsoluteTokens_ReturnsAbsoluteValue()
    {
        // Arrange
        var config = new ThresholdConfig { AbsoluteTokens = 50000 };

        // Act
        var result = config.CalculateThreshold(100000);

        // Assert
        result.Should().Be(50000);
    }

    [Fact]
    public void ThresholdConfig_CalculateThreshold_PercentageTakesPrecedence()
    {
        // Arrange
        var config = new ThresholdConfig { Percentage = 80, AbsoluteTokens = 50000 };

        // Act
        var result = config.CalculateThreshold(100000);

        // Assert
        result.Should().Be(80000); // Percentage takes precedence
    }

    [Fact]
    public void ThresholdConfig_CalculateThreshold_NoValue_ReturnsMax()
    {
        // Arrange
        var config = new ThresholdConfig();

        // Act
        var result = config.CalculateThreshold(100000);

        // Assert
        result.Should().Be(100000); // Returns maxTokens when no threshold set
    }

    [Fact]
    public void TokenBudgetConfig_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var config = new TokenBudgetConfig();

        // Assert
        config.WarningThreshold.Percentage.Should().Be(80);
        config.CompactionThreshold.Percentage.Should().Be(90);
        config.CompactionStrategy.Should().Be(CompactionStrategyType.SlidingWindow);
        config.SlidingWindowKeepTokens.Should().Be(20000);
        config.SummaryTargetTokens.Should().Be(4000);
        config.AutoCompactionEnabled.Should().BeTrue();
    }
}

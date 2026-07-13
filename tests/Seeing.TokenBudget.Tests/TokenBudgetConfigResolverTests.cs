using FluentAssertions;
using Seeing.TokenBudget;
using Xunit;

namespace Seeing.TokenBudget.Tests;

public class TokenBudgetConfigResolverTests
{
    private readonly TokenBudgetConfigResolver _resolver;

    public TokenBudgetConfigResolverTests()
    {
        _resolver = new TokenBudgetConfigResolver();
    }

    [Fact]
    public void Resolve_SessionOverridesAgent()
    {
        // Arrange
        var sessionConfig = new TokenBudgetConfig { MaxContextTokens = 60000 };
        var agentConfig = new TokenBudgetConfig { MaxContextTokens = 80000 };
        var globalConfig = new TokenBudgetConfig { MaxContextTokens = 100000 };

        // Act
        var result = _resolver.Resolve(sessionConfig, agentConfig, globalConfig);

        // Assert
        result.MaxContextTokens.Should().Be(60000);
    }

    [Fact]
    public void Resolve_AgentOverridesGlobal()
    {
        // Arrange
        var agentConfig = new TokenBudgetConfig { MaxContextTokens = 80000 };
        var globalConfig = new TokenBudgetConfig { MaxContextTokens = 100000 };

        // Act
        var result = _resolver.Resolve(null, agentConfig, globalConfig);

        // Assert
        result.MaxContextTokens.Should().Be(80000);
    }

    [Fact]
    public void Resolve_NullValuesUseParent()
    {
        // Arrange
        var sessionConfig = new TokenBudgetConfig 
        { 
            MaxContextTokens = 80000 
        };
        var globalConfig = new TokenBudgetConfig 
        { 
            WarningThreshold = new ThresholdConfig { Percentage = 70 }
        };

        // Act
        var result = _resolver.Resolve(sessionConfig, null, globalConfig);

        // Assert
        result.MaxContextTokens.Should().Be(80000);
        result.WarningThreshold.Percentage.Should().Be(70);
    }

    [Fact]
    public void Resolve_AllNull_ReturnsDefault()
    {
        // Act
        var result = _resolver.Resolve(null, null, null);

        // Assert
        result.Should().NotBeNull();
        result.WarningThreshold.Percentage.Should().Be(80);
        result.CompactionThreshold.Percentage.Should().Be(90);
        result.CompactionStrategy.Should().Be(CompactionStrategyType.SlidingWindow);
        result.AutoCompactionEnabled.Should().BeTrue();
    }

    [Fact]
    public void Resolve_ThresholdMerging_UsesOverrideIfPercentageSet()
    {
        // Arrange
        var baseConfig = new TokenBudgetConfig 
        { 
            WarningThreshold = new ThresholdConfig { Percentage = 70 },
            CompactionThreshold = new ThresholdConfig { Percentage = 85 }
        };
        var overrideConfig = new TokenBudgetConfig 
        { 
            WarningThreshold = new ThresholdConfig { Percentage = 90 }
        };

        // Act
        var result = _resolver.Resolve(overrideConfig, null, baseConfig);

        // Assert
        result.WarningThreshold.Percentage.Should().Be(90);
        result.CompactionThreshold.Percentage.Should().Be(85);
    }

    [Fact]
    public void Resolve_ThresholdMerging_UsesBaseIfOverridePercentageNotSet()
    {
        // Arrange
        var baseConfig = new TokenBudgetConfig 
        { 
            WarningThreshold = new ThresholdConfig { Percentage = 70 }
        };
        var overrideConfig = new TokenBudgetConfig 
        { 
            WarningThreshold = new ThresholdConfig { AbsoluteTokens = 50000 }
        };

        // Act
        var result = _resolver.Resolve(overrideConfig, null, baseConfig);

        // Assert
        result.WarningThreshold.Percentage.Should().Be(70);
        result.WarningThreshold.AbsoluteTokens.Should().Be(50000);
    }

    [Fact]
    public void Resolve_MergesAllLevels()
    {
        // Arrange
        var sessionConfig = new TokenBudgetConfig 
        { 
            MaxContextTokens = 60000,
            WarningThreshold = new ThresholdConfig { Percentage = 75 }
        };
        var agentConfig = new TokenBudgetConfig 
        { 
            MaxContextTokens = 80000,
            CompactionStrategy = CompactionStrategyType.Summarizing
        };
        var globalConfig = new TokenBudgetConfig 
        { 
            MaxContextTokens = 100000,
            SlidingWindowKeepTokens = 30000,
            AutoCompactionEnabled = false
        };

        // Act
        var result = _resolver.Resolve(sessionConfig, agentConfig, globalConfig);

        // Assert
        result.MaxContextTokens.Should().Be(60000); // from session
        result.WarningThreshold.Percentage.Should().Be(75); // from session
        result.CompactionStrategy.Should().Be(CompactionStrategyType.Summarizing); // from agent
        result.SlidingWindowKeepTokens.Should().Be(30000); // from global
        result.AutoCompactionEnabled.Should().BeFalse(); // from global
    }

    [Fact]
    public void Resolve_AgentMergesWithGlobal()
    {
        // Arrange
        var agentConfig = new TokenBudgetConfig 
        { 
            MaxContextTokens = 80000 
        };
        var globalConfig = new TokenBudgetConfig 
        { 
            MaxContextTokens = 100000,
            WarningThreshold = new ThresholdConfig { Percentage = 70 }
        };

        // Act
        var result = _resolver.Resolve(null, agentConfig, globalConfig);

        // Assert
        result.MaxContextTokens.Should().Be(80000); // from agent
        result.WarningThreshold.Percentage.Should().Be(70); // from global (inherited)
    }
}

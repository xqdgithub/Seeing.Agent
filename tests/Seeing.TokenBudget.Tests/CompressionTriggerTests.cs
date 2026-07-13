using FluentAssertions;
using Seeing.Session.Core;
using Seeing.TokenBudget;
using Xunit;

namespace Seeing.TokenBudget.Tests;

/// <summary>
/// Tests for compression trigger with dual-threshold logic.
/// </summary>
public class CompressionTriggerTests
{
    private readonly DefaultCompressionTrigger _trigger;

    public CompressionTriggerTests()
    {
        _trigger = new DefaultCompressionTrigger();
    }

    [Fact]
    public void ShouldTrigger_NormalLevel_ReturnsNoCompression()
    {
        // Arrange
        var status = new BudgetStatus
        {
            CurrentTokens = 5000,
            MaxTokens = 20000,
            Level = BudgetLevel.Normal
        };

        // Act
        var decision = _trigger.ShouldTrigger(status);

        // Assert
        decision.NeedsCompression.Should().BeFalse();
        decision.Reason.Should().Be(CompressionReason.None);
        decision.Immediate.Should().BeFalse();
        decision.Message.Should().BeNull();
    }

    [Fact]
    public void ShouldTrigger_WarningLevel_ReturnsWarningOnly()
    {
        // Arrange
        var status = new BudgetStatus
        {
            CurrentTokens = 15000,
            MaxTokens = 20000,
            Level = BudgetLevel.Warning
        };

        // Act
        var decision = _trigger.ShouldTrigger(status);

        // Assert
        decision.NeedsCompression.Should().BeFalse();
        decision.Reason.Should().Be(CompressionReason.ApproachingLimit);
        decision.Immediate.Should().BeFalse();
        decision.Message.Should().Contain("75%");
    }

    [Fact]
    public void ShouldTrigger_CriticalLevel_ReturnsDeferredCompression()
    {
        // Arrange
        var status = new BudgetStatus
        {
            CurrentTokens = 18000,
            MaxTokens = 20000,
            Level = BudgetLevel.Critical
        };

        // Act
        var decision = _trigger.ShouldTrigger(status);

        // Assert
        decision.NeedsCompression.Should().BeTrue();
        decision.Reason.Should().Be(CompressionReason.OverThreshold);
        decision.Immediate.Should().BeFalse();
        decision.Message.Should().NotBeNull();
    }

    [Fact]
    public void ShouldTrigger_OverflowLevel_ReturnsImmediateCompression()
    {
        // Arrange
        var status = new BudgetStatus
        {
            CurrentTokens = 21000,
            MaxTokens = 20000,
            Level = BudgetLevel.Overflow
        };

        // Act
        var decision = _trigger.ShouldTrigger(status);

        // Assert
        decision.NeedsCompression.Should().BeTrue();
        decision.Reason.Should().Be(CompressionReason.OverMaxLimit);
        decision.Immediate.Should().BeTrue();
        decision.Message.Should().NotBeNull();
    }
}

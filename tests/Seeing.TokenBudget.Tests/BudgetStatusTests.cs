using FluentAssertions;
using Seeing.Session.Core;
using Seeing.TokenBudget;
using Xunit;

namespace Seeing.TokenBudget.Tests;

public class BudgetStatusTests
{
    [Fact]
    public void BudgetStatus_UsagePercentage_CalculatesCorrectly()
    {
        // Arrange & Act
        var status = new BudgetStatus
        {
            CurrentTokens = 50000,
            MaxTokens = 100000,
            Level = BudgetLevel.Normal
        };

        // Assert
        status.UsagePercentage.Should().Be(50.0);
    }

    [Fact]
    public void BudgetStatus_AvailableTokens_ReturnsRemaining()
    {
        // Arrange & Act
        var status = new BudgetStatus
        {
            CurrentTokens = 30000,
            MaxTokens = 100000,
            Level = BudgetLevel.Normal
        };

        // Assert
        status.AvailableTokens.Should().Be(70000);
    }

    [Fact]
    public void BudgetStatus_AvailableTokens_CapsAtZero()
    {
        // Arrange & Act
        var status = new BudgetStatus
        {
            CurrentTokens = 150000,
            MaxTokens = 100000,
            Level = BudgetLevel.Overflow
        };

        // Assert
        status.AvailableTokens.Should().Be(0);
    }

    [Fact]
    public void BudgetStatus_UsagePercentage_ZeroMaxTokens_ReturnsZero()
    {
        // Arrange & Act
        var status = new BudgetStatus
        {
            CurrentTokens = 50000,
            MaxTokens = 0,
            Level = BudgetLevel.Overflow
        };

        // Assert
        status.UsagePercentage.Should().Be(0);
    }
}

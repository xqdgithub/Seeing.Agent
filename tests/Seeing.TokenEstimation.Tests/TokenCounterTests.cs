using FluentAssertions;
using Seeing.TokenEstimation;
using Xunit;

namespace Seeing.TokenEstimation.Tests;

public class TokenCounterTests
{
    [Fact]
    public void Estimate_EmptyString_ReturnsZero()
    {
        // Arrange
        var counter = new CharBasedTokenCounter();

        // Act
        var result = counter.Estimate(string.Empty);

        // Assert
        result.Should().Be(0);
    }

    [Fact]
    public void Estimate_NullString_ReturnsZero()
    {
        // Arrange
        var counter = new CharBasedTokenCounter();

        // Act
        var result = counter.Estimate(null!);

        // Assert
        result.Should().Be(0);
    }

    [Theory]
    [InlineData("hello world", 3)]           // 11 chars / 4 ≈ 3 (rounded)
    [InlineData("test", 1)]                   // 4 chars / 4 = 1
    [InlineData("a very long string here", 6)]  // 23 chars / 4 ≈ 6 (rounded)
    public void Estimate_VariousStrings_ReturnsExpected(string input, int expected)
    {
        // Arrange
        var counter = new CharBasedTokenCounter();

        // Act
        var result = counter.Estimate(input);

        // Assert
        result.Should().Be(expected);
    }
}
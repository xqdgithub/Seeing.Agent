using FluentAssertions;
using Seeing.TokenEstimation;
using Xunit;

namespace Seeing.TokenEstimation.Tests;

public class TokenBreakdownTests
{
    [Fact]
    public void SourceBreakdown_Total_ReturnsSum()
    {
        // Arrange
        var breakdown = new SourceBreakdown
        {
            SystemPrompt = 100,
            ToolDefinitions = 200,
            UserMessages = 300,
            AssistantMessages = 400,
            ToolResults = 500
        };

        // Act
        var total = breakdown.Total;

        // Assert
        total.Should().Be(1500);
    }

    [Fact]
    public void RoleBreakdown_Total_ReturnsSum()
    {
        // Arrange
        var breakdown = new RoleBreakdown
        {
            System = 100,
            User = 200,
            Assistant = 300,
            Tool = 400
        };

        // Act
        var total = breakdown.Total;

        // Assert
        total.Should().Be(1000);
    }

    [Fact]
    public void SourceBreakdown_ToDictionary_ReturnsCorrectKeys()
    {
        // Arrange
        var breakdown = new SourceBreakdown
        {
            SystemPrompt = 100,
            ToolDefinitions = 200,
            UserMessages = 300,
            AssistantMessages = 400,
            ToolResults = 500
        };

        // Act
        var dict = breakdown.ToDictionary();

        // Assert
        dict.Should().ContainKey("system-prompt").WhoseValue.Should().Be(100);
        dict.Should().ContainKey("tool-definitions").WhoseValue.Should().Be(200);
        dict.Should().ContainKey("user-messages").WhoseValue.Should().Be(300);
        dict.Should().ContainKey("assistant-messages").WhoseValue.Should().Be(400);
        dict.Should().ContainKey("tool-results").WhoseValue.Should().Be(500);
    }

    [Fact]
    public void RoleBreakdown_ToDictionary_ReturnsCorrectKeys()
    {
        // Arrange
        var breakdown = new RoleBreakdown
        {
            System = 100,
            User = 200,
            Assistant = 300,
            Tool = 400
        };

        // Act
        var dict = breakdown.ToDictionary();

        // Assert
        dict.Should().ContainKey("system").WhoseValue.Should().Be(100);
        dict.Should().ContainKey("user").WhoseValue.Should().Be(200);
        dict.Should().ContainKey("assistant").WhoseValue.Should().Be(300);
        dict.Should().ContainKey("tool").WhoseValue.Should().Be(400);
    }

    [Fact]
    public void TokenBreakdown_Total_ReturnsBySourceTotal()
    {
        // Arrange
        var breakdown = new TokenBreakdown
        {
            BySource = new SourceBreakdown
            {
                SystemPrompt = 100,
                ToolDefinitions = 200,
                UserMessages = 300,
                AssistantMessages = 400,
                ToolResults = 500
            }
        };

        // Act
        var total = breakdown.Total;

        // Assert
        total.Should().Be(1500);
    }

    [Fact]
    public void TokenBreakdown_BySource_And_ByRole_AreInitialized()
    {
        // Arrange & Act
        var breakdown = new TokenBreakdown();

        // Assert
        breakdown.BySource.Should().NotBeNull();
        breakdown.ByRole.Should().NotBeNull();
    }
}

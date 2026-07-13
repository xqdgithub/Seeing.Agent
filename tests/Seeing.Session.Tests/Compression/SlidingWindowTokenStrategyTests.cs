using FluentAssertions;
using Moq;
using Seeing.Session.Compression.Strategies;
using Seeing.Session.Core;
using Seeing.TokenEstimation;
using Xunit;

namespace Seeing.Session.Tests.Compression;

/// <summary>
/// Unit tests for SlidingWindowTokenStrategy.
/// </summary>
public class SlidingWindowTokenStrategyTests
{
    private readonly SlidingWindowTokenStrategy _strategy;
    private readonly Mock<ITokenCounter> _mockTokenCounter;

    public SlidingWindowTokenStrategyTests()
    {
        _strategy = new SlidingWindowTokenStrategy(keepLastN: 20);
        _mockTokenCounter = new Mock<ITokenCounter>();
    }

    [Fact]
    public void Name_ReturnsCorrectName()
    {
        // Assert
        _strategy.Name.Should().Be("sliding-window-token");
    }

    [Fact]
    public void Compress_WithEmptyMessages_ReturnsEmpty()
    {
        // Arrange
        var messages = Array.Empty<SessionMessage>();

        // Act
        var result = _strategy.Compress(messages);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Compress_WithFewMessages_ReturnsAll()
    {
        // Arrange
        var messages = new List<SessionMessage>
        {
            SessionMessage.SystemMessage("System"),
            SessionMessage.UserMessage("Hello"),
            SessionMessage.AssistantMessage("Hi")
        };

        // Act
        var result = _strategy.Compress(messages);

        // Assert
        result.Should().HaveCount(3);
    }

    [Fact]
    public void Compress_WithManyMessages_PreservesFirstAndLast()
    {
        // Arrange
        var messages = new List<SessionMessage>
        {
            SessionMessage.SystemMessage("System prompt")
        };

        for (var i = 0; i < 30; i++)
        {
            messages.Add(SessionMessage.UserMessage($"Message {i}"));
        }

        // Act
        var result = _strategy.Compress(messages);

        // Assert
        result.Should().HaveCount(21); // 1 first + 20 last
        result[0].Content.Should().Be("System prompt");
        result[^1].Content.Should().Be("Message 29");
    }

    [Fact]
    public void EstimateRetainedCount_WithFewMessages_ReturnsSameCount()
    {
        // Act
        var result = _strategy.EstimateRetainedCount(10);

        // Assert
        result.Should().Be(10);
    }

    [Fact]
    public void EstimateRetainedCount_WithManyMessages_ReturnsFirstPlusLastN()
    {
        // Act
        var result = _strategy.EstimateRetainedCount(50);

        // Assert
        result.Should().Be(21); // 1 first + 20 last
    }

    [Fact]
    public void CompressByTokenBudget_PreservesFirstMessage()
    {
        // Arrange
        _mockTokenCounter.Setup(c => c.Estimate(It.IsAny<string>()))
            .Returns((string s) => s.Length / 4);

        var config = new TokenBudgetConfig
        {
            MaxContextTokens = 1000,
            SlidingWindowKeepTokens = 500
        };

        var messages = new List<SessionMessage>
        {
            SessionMessage.SystemMessage("System prompt that should be preserved"),
            SessionMessage.UserMessage("User message 1"),
            SessionMessage.AssistantMessage("Assistant response 1"),
            SessionMessage.UserMessage("User message 2"),
            SessionMessage.AssistantMessage("Assistant response 2")
        };

        // Act
        var result = _strategy.CompressByTokenBudget(messages, config, _mockTokenCounter.Object);

        // Assert
        result.Success.Should().BeTrue();
        result.CompressedMessages.Should().NotBeEmpty();
        result.CompressedMessages[0].Role.Should().Be(MessageRole.System);
        result.CompressedMessages[0].Content.Should().Be("System prompt that should be preserved");
    }

    [Fact]
    public void CompressByTokenBudget_KeepsRecentMessages()
    {
        // Arrange
        _mockTokenCounter.Setup(c => c.Estimate(It.IsAny<string>()))
            .Returns((string s) => s.Length / 4);

        var config = new TokenBudgetConfig
        {
            MaxContextTokens = 1000,
            SlidingWindowKeepTokens = 200
        };

        var messages = new List<SessionMessage>
        {
            SessionMessage.SystemMessage("System"),
            SessionMessage.UserMessage("Message 1"),
            SessionMessage.AssistantMessage("Response 1"),
            SessionMessage.UserMessage("Message 2"),
            SessionMessage.AssistantMessage("Response 2"),
            SessionMessage.UserMessage("Message 3"),
            SessionMessage.AssistantMessage("Response 3")
        };

        // Act
        var result = _strategy.CompressByTokenBudget(messages, config, _mockTokenCounter.Object);

        // Assert
        result.Success.Should().BeTrue();
        result.CompressedMessages.Should().NotBeEmpty();
        // Most recent message should be present
        result.CompressedMessages[^1].Content.Should().Be("Response 3");
    }

    [Fact]
    public void CompressByTokenBudget_EmptyMessages_ReturnsSuccess()
    {
        // Arrange
        var config = new TokenBudgetConfig
        {
            MaxContextTokens = 1000,
            SlidingWindowKeepTokens = 500
        };

        var messages = Array.Empty<SessionMessage>();

        // Act
        var result = _strategy.CompressByTokenBudget(messages, config, _mockTokenCounter.Object);

        // Assert
        result.Success.Should().BeTrue();
        result.TokensBefore.Should().Be(0);
        result.TokensAfter.Should().Be(0);
        result.MessagesRemoved.Should().Be(0);
        result.CompressedMessages.Should().BeEmpty();
    }

    [Fact]
    public void CompressByTokenBudget_WhenUnderLimit_ReturnsAllMessages()
    {
        // Arrange
        _mockTokenCounter.Setup(c => c.Estimate(It.IsAny<string>()))
            .Returns((string s) => s.Length / 4);

        var config = new TokenBudgetConfig
        {
            MaxContextTokens = 10000,
            SlidingWindowKeepTokens = 5000
        };

        var messages = new List<SessionMessage>
        {
            SessionMessage.SystemMessage("System"),
            SessionMessage.UserMessage("Hello"),
            SessionMessage.AssistantMessage("Hi there")
        };

        // Act
        var result = _strategy.CompressByTokenBudget(messages, config, _mockTokenCounter.Object);

        // Assert
        result.Success.Should().BeTrue();
        result.MessagesRemoved.Should().Be(0);
        result.CompressedMessages.Should().HaveCount(3);
    }

    [Fact]
    public void CompressByTokenBudget_CalculatesTokensCorrectly()
    {
        // Arrange
        _mockTokenCounter.Setup(c => c.Estimate(It.IsAny<string>()))
            .Returns((string s) => s.Length);

        var config = new TokenBudgetConfig
        {
            MaxContextTokens = 100,
            SlidingWindowKeepTokens = 50
        };

        var messages = new List<SessionMessage>
        {
            SessionMessage.SystemMessage("System prompt"), // 13 tokens
            SessionMessage.UserMessage("This is a longer user message"), // 29 tokens
            SessionMessage.AssistantMessage("Short response") // 14 tokens
        };

        // Act
        var result = _strategy.CompressByTokenBudget(messages, config, _mockTokenCounter.Object);

        // Assert
        result.Success.Should().BeTrue();
        // Total: 13 + 29 + 14 = 56 tokens
        result.TokensBefore.Should().Be(56);
        result.TokensAfter.Should().BeLessOrEqualTo(50);
    }

    [Fact]
    public void CompressByTokenBudget_WithReasoningContent_CountsReasoningTokens()
    {
        // Arrange
        _mockTokenCounter.Setup(c => c.Estimate(It.IsAny<string>()))
            .Returns((string s) => s.Length);

        var config = new TokenBudgetConfig
        {
            MaxContextTokens = 100,
            SlidingWindowKeepTokens = 40
        };

        var messages = new List<SessionMessage>
        {
            SessionMessage.SystemMessage("System"),
            SessionMessage.AssistantMessageWithReasoning(
                content: "Response",
                reasoning: "This is a reasoning content")
        };

        // Act
        var result = _strategy.CompressByTokenBudget(messages, config, _mockTokenCounter.Object);

        // Assert
        result.Success.Should().BeTrue();
        _mockTokenCounter.Verify(c => c.Estimate("This is a reasoning content"), Times.AtLeastOnce);
    }

    [Fact]
    public void CompressByTokenBudget_WithToolCalls_CountsToolCallTokens()
    {
        // Arrange
        _mockTokenCounter.Setup(c => c.Estimate(It.IsAny<string>()))
            .Returns((string s) => s.Length);

        var config = new TokenBudgetConfig
        {
            MaxContextTokens = 100,
            SlidingWindowKeepTokens = 50
        };

        var toolCalls = new List<SessionToolCall>
        {
            new() { Name = "test_function", Arguments = "{\"arg\": \"value\"}" }
        };

        var messages = new List<SessionMessage>
        {
            SessionMessage.SystemMessage("System"),
            SessionMessage.AssistantMessageWithToolCalls(toolCalls, "Using tool")
        };

        // Act
        var result = _strategy.CompressByTokenBudget(messages, config, _mockTokenCounter.Object);

        // Assert
        result.Success.Should().BeTrue();
        _mockTokenCounter.Verify(c => c.Estimate("test_function"), Times.AtLeastOnce);
        _mockTokenCounter.Verify(c => c.Estimate("{\"arg\": \"value\"}"), Times.AtLeastOnce);
    }

    [Fact]
    public void CompressByTokenBudget_WithCustomKeepLastN_RespectsSetting()
    {
        // Arrange
        var strategy = new SlidingWindowTokenStrategy(keepLastN: 5);
        _mockTokenCounter.Setup(c => c.Estimate(It.IsAny<string>()))
            .Returns((string s) => s.Length);

        var config = new TokenBudgetConfig
        {
            MaxContextTokens = null, // No limit, should use count-based
            SlidingWindowKeepTokens = 10000
        };

        var messages = new List<SessionMessage>
        {
            SessionMessage.SystemMessage("System")
        };

        for (var i = 0; i < 20; i++)
        {
            messages.Add(SessionMessage.UserMessage($"Message {i}"));
        }

        // Act - using Compress (count-based)
        var result = strategy.Compress(messages);

        // Assert
        result.Should().HaveCount(6); // 1 first + 5 last
    }
}

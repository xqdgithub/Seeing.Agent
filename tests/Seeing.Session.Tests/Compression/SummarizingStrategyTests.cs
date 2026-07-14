using FluentAssertions;
using Moq;
using Seeing.Session.Compression;
using Seeing.Session.Compression.Strategies;
using Seeing.Session.Core;
using Seeing.TokenEstimation;
using Xunit;

namespace Seeing.Session.Tests.Compression;

public class SummarizingStrategyTests
{
    private readonly Mock<ISummarizer> _summarizerMock;
    private readonly Mock<ITokenCounter> _tokenCounterMock;
    private readonly SummarizingStrategy _strategy;

    public SummarizingStrategyTests()
    {
        _summarizerMock = new Mock<ISummarizer>();
        _tokenCounterMock = new Mock<ITokenCounter>();
        _tokenCounterMock.Setup(x => x.Estimate(It.IsAny<string>())).Returns(10);
        _strategy = new SummarizingStrategy(_summarizerMock.Object);
    }

    [Fact]
    public void Name_ReturnsCorrectValue()
    {
        _strategy.Name.Should().Be("summarizing");
    }

    [Fact]
    public void Compress_EmptyMessages_ReturnsEmptyResult()
    {
        var messages = Array.Empty<SessionMessage>();
        var config = new TokenBudgetConfig { SummaryTargetTokens = 1000 };

        var result = _strategy.CompressByTokenBudget(messages, config, _tokenCounterMock.Object);

        result.Success.Should().BeTrue();
        result.MessagesRemoved.Should().Be(0);
    }

    [Fact]
    public void Compress_FewMessages_ReturnsAll()
    {
        var messages = new List<SessionMessage>
        {
            SessionMessage.SystemMessage("System"),
            SessionMessage.UserMessage("Hello"),
            SessionMessage.AssistantMessage("Hi")
        };
        var config = new TokenBudgetConfig { SummaryTargetTokens = 100 };

        var result = _strategy.CompressByTokenBudget(messages, config, _tokenCounterMock.Object);

        result.Success.Should().BeTrue();
        result.MessagesRemoved.Should().Be(0);
        result.CompressedMessages.Should().HaveCount(3);
    }

    [Fact]
    public void CompressByTokenBudget_PreservesFirstAndRecentMessages()
    {
        // Arrange - need more messages than keepRecentMessages + 1 to trigger compression
        var messages = new List<SessionMessage>
        {
            SessionMessage.SystemMessage("System prompt"),
            SessionMessage.UserMessage("User 1"),
            SessionMessage.AssistantMessage("Assistant 1"),
            SessionMessage.UserMessage("User 2"),
            SessionMessage.AssistantMessage("Assistant 2"),
            SessionMessage.UserMessage("User 3"),
            SessionMessage.AssistantMessage("Assistant 3"),
        };
        var config = new TokenBudgetConfig { SummaryTargetTokens = 100 };

        // Setup summarizer to return a summary
        SetupSummarizer("Summary of conversation");

        // Setup token counter to return high values so compression is triggered
        _tokenCounterMock.Setup(x => x.Estimate(It.IsAny<string>())).Returns(50);

        // Act
        var result = _strategy.CompressByTokenBudget(messages, config, _tokenCounterMock.Object);

        // Assert
        result.Success.Should().BeTrue();
        result.MessagesRemoved.Should().BeGreaterThan(0);
        // Should have: system prompt + summary + recent messages (default 4)
        result.CompressedMessages.Count.Should().BeLessThan(messages.Count);
    }

    [Fact]
    public void CompressByTokenBudget_GeneratesSummary()
    {
        // Arrange
        var messages = new List<SessionMessage>
        {
            SessionMessage.SystemMessage("System prompt"),
            SessionMessage.UserMessage("User 1"),
            SessionMessage.AssistantMessage("Assistant 1"),
            SessionMessage.UserMessage("User 2"),
            SessionMessage.AssistantMessage("Assistant 2"),
            SessionMessage.UserMessage("User 3"),
            SessionMessage.AssistantMessage("Assistant 3"),
        };

        var config = new TokenBudgetConfig { SummaryTargetTokens = 50 };

        // Setup summarizer to return a summary
        SetupSummarizer("Generated summary");

        // Setup token counter - high values trigger compression
        _tokenCounterMock.Setup(x => x.Estimate(It.IsAny<string>())).Returns(30);

        // Act
        var result = _strategy.CompressByTokenBudget(messages, config, _tokenCounterMock.Object);

        // Assert
        result.Success.Should().BeTrue();
        _summarizerMock.Verify(x => x.SummarizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void CompressByTokenBudget_WhenTokensUnderTarget_ReturnsAllMessages()
    {
        // Arrange
        var messages = new List<SessionMessage>
        {
            SessionMessage.SystemMessage("System"),
            SessionMessage.UserMessage("User 1"),
            SessionMessage.AssistantMessage("Assistant 1"),
        };
        var config = new TokenBudgetConfig { SummaryTargetTokens = 1000 };

        // Setup token counter - low values mean no compression needed
        _tokenCounterMock.Setup(x => x.Estimate(It.IsAny<string>())).Returns(10);

        // Act
        var result = _strategy.CompressByTokenBudget(messages, config, _tokenCounterMock.Object);

        // Assert
        result.Success.Should().BeTrue();
        result.MessagesRemoved.Should().Be(0);
        result.CompressedMessages.Should().HaveCount(3);
    }

    [Fact]
    public void CompressByTokenBudget_OnException_ReturnsFailedResult()
    {
        // Arrange
        var messages = new List<SessionMessage>
        {
            SessionMessage.SystemMessage("System prompt"),
            SessionMessage.UserMessage("User 1"),
            SessionMessage.AssistantMessage("Assistant 1"),
            SessionMessage.UserMessage("User 2"),
            SessionMessage.AssistantMessage("Assistant 2"),
            SessionMessage.UserMessage("User 3"),
            SessionMessage.AssistantMessage("Assistant 3"),
        };

        var config = new TokenBudgetConfig { SummaryTargetTokens = 50 };

        // Setup summarizer to throw exception
        _summarizerMock
            .Setup(x => x.SummarizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("LLM error"));

        // Setup token counter - high values trigger compression
        _tokenCounterMock.Setup(x => x.Estimate(It.IsAny<string>())).Returns(30);

        // Act
        var result = _strategy.CompressByTokenBudget(messages, config, _tokenCounterMock.Object);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("LLM error");
    }

    [Fact]
    public void Compress_SimpleSlidingWindow_PreservesFirstAndLastN()
    {
        // Arrange
        var messages = new List<SessionMessage>
        {
            SessionMessage.SystemMessage("System prompt")
        };

        for (var i = 0; i < 10; i++)
        {
            messages.Add(SessionMessage.UserMessage($"Message {i}"));
        }

        // Act
        var result = _strategy.Compress(messages);

        // Assert - default keepRecentMessages is 4
        result.Should().HaveCount(5); // 1 first + 4 recent
        result[0].Content.Should().Be("System prompt");
    }

    [Fact]
    public void EstimateRetainedCount_WithFewMessages_ReturnsSameCount()
    {
        // Act
        var result = _strategy.EstimateRetainedCount(3);

        // Assert
        result.Should().Be(3);
    }

    [Fact]
    public void EstimateRetainedCount_WithManyMessages_ReturnsFirstPlusRecent()
    {
        // Act - default keepRecentMessages is 4
        var result = _strategy.EstimateRetainedCount(20);

        // Assert
        result.Should().Be(5); // 1 first + 4 recent
    }

    [Fact]
    public void Constructor_WithCustomKeepRecentMessages_UsesCustomValue()
    {
        // Arrange
        var strategy = new SummarizingStrategy(_summarizerMock.Object, keepRecentMessages: 6);

        // Act
        var result = strategy.EstimateRetainedCount(20);

        // Assert
        result.Should().Be(7); // 1 first + 6 recent
    }

    private void SetupSummarizer(string summary)
    {
        _summarizerMock
            .Setup(x => x.SummarizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(summary);
    }
}

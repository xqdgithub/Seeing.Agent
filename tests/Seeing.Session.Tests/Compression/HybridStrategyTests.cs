using FluentAssertions;
using Moq;
using Seeing.Session.Compression;
using Seeing.Session.Compression.Strategies;
using Seeing.Session.Core;
using Seeing.TokenEstimation;
using Xunit;

namespace Seeing.Session.Tests.Compression;

public class HybridStrategyTests
{
    private readonly Mock<ITokenCounter> _tokenCounterMock;
    private readonly HybridStrategy _strategy;

    public HybridStrategyTests()
    {
        _tokenCounterMock = new Mock<ITokenCounter>();
        _tokenCounterMock.Setup(x => x.Estimate(It.IsAny<string>())).Returns(10);
        
        var slidingWindow = new SlidingWindowTokenStrategy(keepLastN: 2);
        var llmMock = new Mock<ISummarizer>();
        llmMock.Setup(x => x.SummarizeAsync(It.IsAny<string>()))
            .ReturnsAsync("Summary of conversation");
        var summarizing = new SummarizingStrategy(llmMock.Object, keepRecentMessages: 2);
        
        _strategy = new HybridStrategy(slidingWindow, summarizing);
    }

    [Fact]
    public void Name_ReturnsCorrectValue()
    {
        _strategy.Name.Should().Be("hybrid");
    }

    [Fact]
    public void CompressByTokenBudget_TriesSlidingWindowFirst()
    {
        // Arrange
        var messages = CreateMessages(10);
        var config = new TokenBudgetConfig
        {
            SlidingWindowKeepTokens = 100,
            SummaryTargetTokens = 50
        };

        // Act
        var result = _strategy.CompressByTokenBudget(messages, config, _tokenCounterMock.Object);

        // Assert
        result.Success.Should().BeTrue();
    }

    [Fact]
    public void CompressByTokenBudget_ReturnsEmptyResultForEmptyMessages()
    {
        var messages = Array.Empty<SessionMessage>();
        var config = new TokenBudgetConfig();

        var result = _strategy.CompressByTokenBudget(messages, config, _tokenCounterMock.Object);

        result.Success.Should().BeTrue();
        result.MessagesRemoved.Should().Be(0);
    }

    [Fact]
    public void Compress_DelegatesToSlidingWindow()
    {
        // Arrange
        var messages = CreateMessages(10);

        // Act
        var result = _strategy.Compress(messages);

        // Assert - HybridStrategy delegates to SlidingWindow which keeps first + last N
        result.Should().NotBeEmpty();
        result[0].Role.Should().Be(MessageRole.System);
    }

    [Fact]
    public void EstimateRetainedCount_DelegatesToSlidingWindow()
    {
        // Act
        var result = _strategy.EstimateRetainedCount(100);

        // Assert - SlidingWindowTokenStrategy with keepLastN=2 returns 1 + 2 = 3
        result.Should().Be(3);
    }

    [Fact]
    public void Constructor_ThrowsOnNullSlidingWindowStrategy()
    {
        // Arrange
        var summarizing = new SummarizingStrategy(Mock.Of<ISummarizer>(), keepRecentMessages: 2);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new HybridStrategy(null!, summarizing));
    }

    [Fact]
    public void Constructor_ThrowsOnNullSummarizingStrategy()
    {
        // Arrange
        var slidingWindow = new SlidingWindowTokenStrategy(keepLastN: 2);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new HybridStrategy(slidingWindow, null!));
    }

    [Fact]
    public void CompressByTokenBudget_FallsBackToSummarizing_WhenSlidingWindowExceedsTarget()
    {
        // Arrange - Set up token counter where sliding window result exceeds target
        var tokenCounterMock = new Mock<ITokenCounter>();
        // First call counts total tokens (high value to trigger compression)
        // Then sliding window keeps messages, but they still exceed target
        // So it should fall back to summarizing
        tokenCounterMock.Setup(x => x.Estimate(It.IsAny<string>())).Returns(100);

        var llmMock = new Mock<ISummarizer>();
        llmMock.Setup(x => x.SummarizeAsync(It.IsAny<string>()))
            .ReturnsAsync("Summary of conversation");

        var slidingWindow = new SlidingWindowTokenStrategy(keepLastN: 2);
        var summarizing = new SummarizingStrategy(llmMock.Object, keepRecentMessages: 1);
        var strategy = new HybridStrategy(slidingWindow, summarizing);

        var messages = new List<SessionMessage>
        {
            SessionMessage.SystemMessage("System prompt"),
            SessionMessage.UserMessage("User message 1"),
            SessionMessage.AssistantMessage("Assistant message 1"),
            SessionMessage.UserMessage("User message 2"),
            SessionMessage.AssistantMessage("Assistant message 2")
        };

        // Set a very low target to ensure sliding window exceeds it
        var config = new TokenBudgetConfig
        {
            SlidingWindowKeepTokens = 50,  // Target is 50, but sliding window result will be ~300 tokens
            SummaryTargetTokens = 30
        };

        // Act
        var result = strategy.CompressByTokenBudget(messages, config, tokenCounterMock.Object);

        // Assert - Should have fallen back to summarizing strategy
        result.Success.Should().BeTrue();
        // Verify that the summarizer was called (proving fallback occurred)
        llmMock.Verify(x => x.SummarizeAsync(It.IsAny<string>()), Times.AtLeastOnce);
    }

    private static List<SessionMessage> CreateMessages(int count)
    {
        var messages = new List<SessionMessage> { SessionMessage.SystemMessage("System") };
        for (var i = 0; i < count - 1; i++)
        {
            messages.Add(i % 2 == 0
                ? SessionMessage.UserMessage($"User message {i}")
                : SessionMessage.AssistantMessage($"Assistant message {i}"));
        }
        return messages;
    }
}

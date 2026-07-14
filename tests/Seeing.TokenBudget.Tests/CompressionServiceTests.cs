using FluentAssertions;
using Moq;
using Seeing.Session.Compression;
using Seeing.Session.Core;
using Seeing.TokenEstimation;
using Xunit;

namespace Seeing.TokenBudget.Tests;

public class CompressionServiceTests
{
    private readonly Mock<ICompressionStrategyFactory> _factoryMock;
    private readonly Mock<ITokenBudgetConfigResolver> _resolverMock;
    private readonly Mock<ICompressionStrategy> _strategyMock;
    private readonly Mock<ITokenCounter> _tokenCounterMock;

    public CompressionServiceTests()
    {
        _factoryMock = new Mock<ICompressionStrategyFactory>();
        _resolverMock = new Mock<ITokenBudgetConfigResolver>();
        _strategyMock = new Mock<ICompressionStrategy>();
        _tokenCounterMock = new Mock<ITokenCounter>();
    }

    [Fact]
    public async Task CompressAsync_WithAutoCompactionDisabled_ReturnsNoCompression()
    {
        // Arrange
        var config = new TokenBudgetConfig { AutoCompactionEnabled = false };
        _resolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Returns(config);

        var service = new CompressionService(_factoryMock.Object, _resolverMock.Object, _tokenCounterMock.Object);
        var session = new SessionData();

        // Act
        var result = await service.CompressAsync(session);

        // Assert
        result.Success.Should().BeTrue();
        result.MessagesRemoved.Should().Be(0);
    }

    [Fact]
    public async Task CompressAsync_WithAutoCompactionEnabled_CallsStrategy()
    {
        // Arrange
        var config = new TokenBudgetConfig 
        { 
            AutoCompactionEnabled = true,
            CompactionStrategy = CompactionStrategyType.SlidingWindow 
        };
        _resolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Returns(config);

        var compressedMessages = new List<SessionMessage>
        {
            SessionMessage.SystemMessage("System prompt"),
            SessionMessage.UserMessage("User message")
        };
        var compressionResult = Seeing.Session.Core.CompressionResult.Succeeded(1000, 500, 3, compressedMessages);

        _strategyMock.Setup(x => x.CompressByTokenBudget(
                It.IsAny<IReadOnlyList<SessionMessage>>(),
                It.IsAny<TokenBudgetConfig>(),
                It.IsAny<ITokenCounter>()))
            .Returns(compressionResult);

        _factoryMock.Setup(x => x.GetStrategy(CompactionStrategyType.SlidingWindow))
            .Returns(_strategyMock.Object);

        var service = new CompressionService(_factoryMock.Object, _resolverMock.Object, _tokenCounterMock.Object);
        var session = new SessionData();
        session.Messages.Add(SessionMessage.SystemMessage("System prompt"));
        session.Messages.Add(SessionMessage.UserMessage("User message 1"));
        session.Messages.Add(SessionMessage.AssistantMessage("Assistant message"));
        session.Messages.Add(SessionMessage.UserMessage("User message 2"));
        session.Messages.Add(SessionMessage.UserMessage("User message 3"));

        // Act
        var result = await service.CompressAsync(session);

        // Assert
        result.Success.Should().BeTrue();
        result.TokensBefore.Should().Be(1000);
        result.TokensAfter.Should().Be(500);
        result.MessagesRemoved.Should().Be(3);
        session.Messages.Should().HaveCount(2);
    }

    [Fact]
    public async Task CompressAsync_WithSessionAndAgentConfig_PassesBoth()
    {
        // Arrange
        var config = new TokenBudgetConfig 
        { 
            AutoCompactionEnabled = true,
            CompactionStrategy = CompactionStrategyType.SlidingWindow,
            MaxContextTokens = 50000
        };
        
        TokenBudgetConfig? capturedSessionConfig = null;
        TokenBudgetConfig? capturedAgentConfig = null;
        
        _resolverMock.Setup(x => x.Resolve(
                It.IsAny<TokenBudgetConfig?>(), 
                It.IsAny<TokenBudgetConfig?>(), 
                It.IsAny<TokenBudgetConfig?>()))
            .Callback<TokenBudgetConfig?, TokenBudgetConfig?, TokenBudgetConfig?>((s, a, g) =>
            {
                capturedSessionConfig = s;
                capturedAgentConfig = a;
            })
            .Returns(config);

        var compressedMessages = new List<SessionMessage>
        {
            SessionMessage.SystemMessage("System")
        };
        var compressionResult = Seeing.Session.Core.CompressionResult.Succeeded(100, 50, 1, compressedMessages);

        _strategyMock.Setup(x => x.CompressByTokenBudget(
                It.IsAny<IReadOnlyList<SessionMessage>>(),
                It.IsAny<TokenBudgetConfig>(),
                It.IsAny<ITokenCounter>()))
            .Returns(compressionResult);

        _factoryMock.Setup(x => x.GetStrategy(CompactionStrategyType.SlidingWindow))
            .Returns(_strategyMock.Object);

        var service = new CompressionService(_factoryMock.Object, _resolverMock.Object, _tokenCounterMock.Object);
        
        var sessionBudget = new TokenBudgetConfig { MaxContextTokens = 60000 };
        var agentBudget = new TokenBudgetConfig { MaxContextTokens = 80000 };
        var session = new SessionData();
        session.Messages.Add(SessionMessage.UserMessage("Test"));

        // Act
        await service.CompressAsync(session, sessionBudget, agentBudget);

        // Assert
        capturedSessionConfig.Should().Be(sessionBudget);
        capturedAgentConfig.Should().Be(agentBudget);
    }

    [Fact]
    public async Task CompressAsync_WhenCompressionFails_DoesNotModifyMessages()
    {
        // Arrange
        var config = new TokenBudgetConfig 
        { 
            AutoCompactionEnabled = true,
            CompactionStrategy = CompactionStrategyType.SlidingWindow 
        };
        _resolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Returns(config);

        var compressionResult = Seeing.Session.Core.CompressionResult.Failed("Compression failed");

        _strategyMock.Setup(x => x.CompressByTokenBudget(
                It.IsAny<IReadOnlyList<SessionMessage>>(),
                It.IsAny<TokenBudgetConfig>(),
                It.IsAny<ITokenCounter>()))
            .Returns(compressionResult);

        _factoryMock.Setup(x => x.GetStrategy(CompactionStrategyType.SlidingWindow))
            .Returns(_strategyMock.Object);

        var service = new CompressionService(_factoryMock.Object, _resolverMock.Object, _tokenCounterMock.Object);
        var session = new SessionData();
        session.Messages.Add(SessionMessage.UserMessage("Message 1"));
        session.Messages.Add(SessionMessage.UserMessage("Message 2"));
        var originalCount = session.Messages.Count;

        // Act
        var result = await service.CompressAsync(session);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Compression failed");
        session.Messages.Should().HaveCount(originalCount);
    }

    [Fact]
    public async Task CompressAsync_UsesCorrectStrategy_ForSummarizingType()
    {
        // Arrange
        var config = new TokenBudgetConfig 
        { 
            AutoCompactionEnabled = true,
            CompactionStrategy = CompactionStrategyType.Summarizing 
        };
        _resolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Returns(config);

        var compressedMessages = new List<SessionMessage>
        {
            SessionMessage.SystemMessage("System"),
            SessionMessage.SystemMessage("[Summary]")
        };
        var compressionResult = Seeing.Session.Core.CompressionResult.Succeeded(1000, 300, 2, compressedMessages);

        _strategyMock.Setup(x => x.CompressByTokenBudget(
                It.IsAny<IReadOnlyList<SessionMessage>>(),
                It.IsAny<TokenBudgetConfig>(),
                It.IsAny<ITokenCounter>()))
            .Returns(compressionResult);

        _factoryMock.Setup(x => x.GetStrategy(CompactionStrategyType.Summarizing))
            .Returns(_strategyMock.Object);

        var service = new CompressionService(_factoryMock.Object, _resolverMock.Object, _tokenCounterMock.Object);
        var session = new SessionData();
        session.Messages.Add(SessionMessage.UserMessage("Test"));

        // Act
        var result = await service.CompressAsync(session);

        // Assert
        _factoryMock.Verify(x => x.GetStrategy(CompactionStrategyType.Summarizing), Times.Once);
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task CompressAsync_UsesCorrectStrategy_ForHybridType()
    {
        // Arrange
        var config = new TokenBudgetConfig 
        { 
            AutoCompactionEnabled = true,
            CompactionStrategy = CompactionStrategyType.Hybrid 
        };
        _resolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Returns(config);

        var compressedMessages = new List<SessionMessage>
        {
            SessionMessage.SystemMessage("System")
        };
        var compressionResult = Seeing.Session.Core.CompressionResult.Succeeded(1000, 400, 3, compressedMessages);

        _strategyMock.Setup(x => x.CompressByTokenBudget(
                It.IsAny<IReadOnlyList<SessionMessage>>(),
                It.IsAny<TokenBudgetConfig>(),
                It.IsAny<ITokenCounter>()))
            .Returns(compressionResult);

        _factoryMock.Setup(x => x.GetStrategy(CompactionStrategyType.Hybrid))
            .Returns(_strategyMock.Object);

        var service = new CompressionService(_factoryMock.Object, _resolverMock.Object, _tokenCounterMock.Object);
        var session = new SessionData();
        session.Messages.Add(SessionMessage.UserMessage("Test"));

        // Act
        var result = await service.CompressAsync(session);

        // Assert
        _factoryMock.Verify(x => x.GetStrategy(CompactionStrategyType.Hybrid), Times.Once);
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task CompressAsync_WithCancellationToken_PassesThrough()
    {
        // Arrange
        var config = new TokenBudgetConfig { AutoCompactionEnabled = false };
        _resolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Returns(config);

        var service = new CompressionService(_factoryMock.Object, _resolverMock.Object, _tokenCounterMock.Object);
        var session = new SessionData();
        using var cts = new CancellationTokenSource();

        // Act & Assert (should not throw)
        var act = async () => await service.CompressAsync(session, cancellationToken: cts.Token);
        await act.Should().NotThrowAsync();
    }
}

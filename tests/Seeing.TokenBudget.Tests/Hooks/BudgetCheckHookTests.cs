using FluentAssertions;
using Moq;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Models;
using Seeing.Agent.TokenBudget;
using Seeing.Session.Core;
using Xunit;

// Alias to disambiguate from Seeing.TokenBudget.CompressionResult
using SessionCompressionResult = Seeing.Session.Core.CompressionResult;

namespace Seeing.TokenBudget.Tests.Hooks;

public class BudgetCheckHookTests
{
    private readonly Mock<ICompressionService> _compressionServiceMock = new();
    private readonly Mock<ITokenBudgetConfigResolver> _configResolverMock = new();

    [Fact]
    public async Task ExecuteAsync_WhenPendingCompactionTrue_TriggersCompression()
    {
        // Arrange
        var session = new SessionData { PendingCompaction = true };
        var agent = new AgentDefinition();
        var payload = CreatePayload(session, agent);

        _configResolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Returns(new TokenBudgetConfig { AutoCompactionEnabled = true });

        _compressionServiceMock.Setup(x => x.CompressAsync(
                It.IsAny<SessionData>(),
                It.IsAny<TokenBudgetConfig?>(),
                It.IsAny<TokenBudgetConfig?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SessionCompressionResult.Succeeded(1000, 500, 5, new List<SessionMessage>()));

        var hook = new BudgetCheckHook(_compressionServiceMock.Object, _configResolverMock.Object);

        // Act
        var result = await hook.ExecuteAsync(payload);

        // Assert
        result.Continue.Should().BeTrue();
        session.PendingCompaction.Should().BeFalse();
        _compressionServiceMock.Verify(x => x.CompressAsync(
            It.IsAny<SessionData>(),
            It.IsAny<TokenBudgetConfig?>(),
            It.IsAny<TokenBudgetConfig?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPendingCompactionFalse_SkipsCompression()
    {
        // Arrange
        var session = new SessionData { PendingCompaction = false };
        var agent = new AgentDefinition();
        var payload = CreatePayload(session, agent);

        var hook = new BudgetCheckHook(_compressionServiceMock.Object, _configResolverMock.Object);

        // Act
        var result = await hook.ExecuteAsync(payload);

        // Assert
        result.Continue.Should().BeTrue();
        _compressionServiceMock.Verify(x => x.CompressAsync(
            It.IsAny<SessionData>(),
            It.IsAny<TokenBudgetConfig?>(),
            It.IsAny<TokenBudgetConfig?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAutoCompactionDisabled_ClearsPendingFlagWithoutCompression()
    {
        // Arrange
        var session = new SessionData { PendingCompaction = true };
        var agent = new AgentDefinition();
        var payload = CreatePayload(session, agent);

        _configResolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Returns(new TokenBudgetConfig { AutoCompactionEnabled = false });

        var hook = new BudgetCheckHook(_compressionServiceMock.Object, _configResolverMock.Object);

        // Act
        var result = await hook.ExecuteAsync(payload);

        // Assert
        result.Continue.Should().BeTrue();
        session.PendingCompaction.Should().BeFalse();
        _compressionServiceMock.Verify(x => x.CompressAsync(
            It.IsAny<SessionData>(),
            It.IsAny<TokenBudgetConfig?>(),
            It.IsAny<TokenBudgetConfig?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_StoresCompressionResultInMutable()
    {
        // Arrange
        var session = new SessionData { PendingCompaction = true };
        var agent = new AgentDefinition();
        var payload = CreatePayload(session, agent);

        var compressionResult = SessionCompressionResult.Succeeded(2000, 800, 3, new List<SessionMessage>());

        _configResolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Returns(new TokenBudgetConfig { AutoCompactionEnabled = true });

        _compressionServiceMock.Setup(x => x.CompressAsync(
                It.IsAny<SessionData>(),
                It.IsAny<TokenBudgetConfig?>(),
                It.IsAny<TokenBudgetConfig?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(compressionResult);

        var hook = new BudgetCheckHook(_compressionServiceMock.Object, _configResolverMock.Object);

        // Act
        await hook.ExecuteAsync(payload);

        // Assert
        payload.Mutable.Should().ContainKey("compactionResult");
        var storedResult = payload.Mutable["compactionResult"] as SessionCompressionResult;
        storedResult.Should().NotBeNull();
        storedResult!.TokensBefore.Should().Be(2000);
        storedResult.TokensAfter.Should().Be(800);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCompressionFails_StoresErrorAndContinues()
    {
        // Arrange
        var session = new SessionData { PendingCompaction = true };
        var agent = new AgentDefinition();
        var payload = CreatePayload(session, agent);

        _configResolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Returns(new TokenBudgetConfig { AutoCompactionEnabled = true });

        _compressionServiceMock.Setup(x => x.CompressAsync(
                It.IsAny<SessionData>(),
                It.IsAny<TokenBudgetConfig?>(),
                It.IsAny<TokenBudgetConfig?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Compression failed"));

        var hook = new BudgetCheckHook(_compressionServiceMock.Object, _configResolverMock.Object);

        // Act
        var result = await hook.ExecuteAsync(payload);

        // Assert
        result.Continue.Should().BeTrue(); // Should not block execution
        payload.Mutable.Should().ContainKey("compactionError");
        payload.Mutable["compactionError"].Should().Be("Compression failed");
    }

    [Fact]
    public async Task ExecuteAsync_WhenSessionIsNull_ReturnsSuccess()
    {
        // Arrange
        var agent = new AgentDefinition();
        var payload = CreatePayload(null!, agent);

        var hook = new BudgetCheckHook(_compressionServiceMock.Object, _configResolverMock.Object);

        // Act
        var result = await hook.ExecuteAsync(payload);

        // Assert
        result.Continue.Should().BeTrue();
        _compressionServiceMock.Verify(x => x.CompressAsync(
            It.IsAny<SessionData>(),
            It.IsAny<TokenBudgetConfig?>(),
            It.IsAny<TokenBudgetConfig?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAgentIsNull_ReturnsSuccess()
    {
        // Arrange
        var session = new SessionData { PendingCompaction = true };
        var payload = CreatePayload(session, null!);

        var hook = new BudgetCheckHook(_compressionServiceMock.Object, _configResolverMock.Object);

        // Act
        var result = await hook.ExecuteAsync(payload);

        // Assert
        result.Continue.Should().BeTrue();
        _compressionServiceMock.Verify(x => x.CompressAsync(
            It.IsAny<SessionData>(),
            It.IsAny<TokenBudgetConfig?>(),
            It.IsAny<TokenBudgetConfig?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Hook_ShouldHaveCorrectSpec()
    {
        // Arrange
        var hook = new BudgetCheckHook(_compressionServiceMock.Object, _configResolverMock.Object);

        // Assert
        hook.Spec.Should().Be(HookRegistry.ChatBeforeStart);
    }

    [Fact]
    public void Hook_ShouldHaveHighPriority()
    {
        // Arrange
        var hook = new BudgetCheckHook(_compressionServiceMock.Object, _configResolverMock.Object);

        // Assert
        hook.Priority.Should().Be(100);
    }

    [Fact]
    public async Task ExecuteAsync_PassesCorrectConfigsToResolver()
    {
        // Arrange
        var sessionConfig = new TokenBudgetConfig { MaxContextTokens = 50000 };
        var agentConfig = new TokenBudgetConfig { MaxContextTokens = 80000 };
        var session = new SessionData { PendingCompaction = true, BudgetConfig = sessionConfig };
        var agent = new AgentDefinition { BudgetConfig = agentConfig };
        var payload = CreatePayload(session, agent);

        TokenBudgetConfig? capturedSessionConfig = null;
        TokenBudgetConfig? capturedAgentConfig = null;

        _configResolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Callback<TokenBudgetConfig?, TokenBudgetConfig?, TokenBudgetConfig?>((s, a, g) =>
            {
                capturedSessionConfig = s;
                capturedAgentConfig = a;
            })
            .Returns(new TokenBudgetConfig { AutoCompactionEnabled = true });

        _compressionServiceMock.Setup(x => x.CompressAsync(
                It.IsAny<SessionData>(),
                It.IsAny<TokenBudgetConfig?>(),
                It.IsAny<TokenBudgetConfig?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SessionCompressionResult.Succeeded(1000, 500, 1, new List<SessionMessage>()));

        var hook = new BudgetCheckHook(_compressionServiceMock.Object, _configResolverMock.Object);

        // Act
        await hook.ExecuteAsync(payload);

        // Assert
        capturedSessionConfig.Should().Be(sessionConfig);
        capturedAgentConfig.Should().Be(agentConfig);
    }

    [Fact]
    public async Task ExecuteAsync_PassesCorrectConfigsToCompressionService()
    {
        // Arrange
        var sessionConfig = new TokenBudgetConfig { MaxContextTokens = 50000 };
        var agentConfig = new TokenBudgetConfig { MaxContextTokens = 80000 };
        var session = new SessionData { PendingCompaction = true, BudgetConfig = sessionConfig };
        var agent = new AgentDefinition { BudgetConfig = agentConfig };
        var payload = CreatePayload(session, agent);

        _configResolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Returns(new TokenBudgetConfig { AutoCompactionEnabled = true });

        TokenBudgetConfig? capturedSessionConfig = null;
        TokenBudgetConfig? capturedAgentConfig = null;

        _compressionServiceMock.Setup(x => x.CompressAsync(
                It.IsAny<SessionData>(),
                It.IsAny<TokenBudgetConfig?>(),
                It.IsAny<TokenBudgetConfig?>(),
                It.IsAny<CancellationToken>()))
            .Callback<SessionData, TokenBudgetConfig?, TokenBudgetConfig?, CancellationToken>((s, sc, ac, ct) =>
            {
                capturedSessionConfig = sc;
                capturedAgentConfig = ac;
            })
            .ReturnsAsync(SessionCompressionResult.Succeeded(1000, 500, 1, new List<SessionMessage>()));

        var hook = new BudgetCheckHook(_compressionServiceMock.Object, _configResolverMock.Object);

        // Act
        await hook.ExecuteAsync(payload);

        // Assert
        capturedSessionConfig.Should().Be(sessionConfig);
        capturedAgentConfig.Should().Be(agentConfig);
    }

    private static HookPayload CreatePayload(SessionData session, AgentDefinition agent)
    {
        return HookPayload.Blocking(
            HookRegistry.ChatBeforeStart,
            "test-session",
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent
            });
    }
}
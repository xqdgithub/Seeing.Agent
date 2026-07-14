using FluentAssertions;
using Moq;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Models;
using Seeing.Agent.TokenBudget;
using Seeing.Session.Core;
using Seeing.TokenBudget.Api.Responses;
using Seeing.TokenEstimation;
using Xunit;

// Alias to disambiguate from Seeing.TokenBudget.CompressionResult
using SessionCompressionResult = Seeing.Session.Core.CompressionResult;

namespace Seeing.TokenBudget.Tests.Hooks;

public class BudgetUpdateHookTests
{
    private readonly Mock<ITokenBudgetManager> _budgetManagerMock = new();
    private readonly Mock<ITokenBudgetConfigResolver> _configResolverMock = new();
    private readonly Mock<ITokenCounter> _tokenCounterMock = new();

    [Fact]
    public async Task ExecuteAsync_WhenOverCriticalThreshold_SetsPendingCompaction()
    {
        // Arrange
        var session = new SessionData();
        session.Messages.Add(SessionMessage.UserMessage("Test message"));
        var agent = new AgentDefinition();

        var config = new TokenBudgetConfig
        {
            MaxContextTokens = 1000,
            WarningThreshold = new ThresholdConfig { Percentage = 80 },
            CompactionThreshold = new ThresholdConfig { Percentage = 90 }
        };

        _configResolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Returns(config);

        _budgetManagerMock.Setup(x => x.CalculateBreakdown(It.IsAny<SessionData>(), It.IsAny<string?>(), It.IsAny<int?>()))
            .Returns(new TokenBreakdown { BySource = new SourceBreakdown { UserMessages = 500 } });

        _budgetManagerMock.Setup(x => x.CheckBudget(It.IsAny<SessionData>(), It.IsAny<TokenBudgetConfig>(), It.IsAny<int>()))
            .Returns(new BudgetStatus { Level = BudgetLevel.Critical, CurrentTokens = 950, MaxTokens = 1000 });

        var payload = HookPayload.FireAndForget(
            HookRegistry.ChatAfterComplete,
            "test-session",
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent
            });

        var hook = new BudgetUpdateHook(_budgetManagerMock.Object, _configResolverMock.Object, _tokenCounterMock.Object);

        // Act
        var result = await hook.ExecuteAsync(payload);

        // Assert
        result.Continue.Should().BeTrue();
        session.PendingCompaction.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WhenNormal_DoesNotSetPendingCompaction()
    {
        // Arrange
        var session = new SessionData();
        var agent = new AgentDefinition();

        var config = new TokenBudgetConfig { MaxContextTokens = 1000 };

        _configResolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Returns(config);

        _budgetManagerMock.Setup(x => x.CalculateBreakdown(It.IsAny<SessionData>(), It.IsAny<string?>(), It.IsAny<int?>()))
            .Returns(new TokenBreakdown());

        _budgetManagerMock.Setup(x => x.CheckBudget(It.IsAny<SessionData>(), It.IsAny<TokenBudgetConfig>(), It.IsAny<int>()))
            .Returns(new BudgetStatus { Level = BudgetLevel.Normal, CurrentTokens = 100, MaxTokens = 1000 });

        var payload = HookPayload.FireAndForget(
            HookRegistry.ChatAfterComplete,
            "test-session",
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent
            });

        var hook = new BudgetUpdateHook(_budgetManagerMock.Object, _configResolverMock.Object, _tokenCounterMock.Object);

        // Act
        var result = await hook.ExecuteAsync(payload);

        // Assert
        result.Continue.Should().BeTrue();
        session.PendingCompaction.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WhenWarningLevel_DoesNotSetPendingCompaction()
    {
        // Arrange
        var session = new SessionData();
        var agent = new AgentDefinition();

        var config = new TokenBudgetConfig
        {
            MaxContextTokens = 1000,
            WarningThreshold = new ThresholdConfig { Percentage = 80 },
            CompactionThreshold = new ThresholdConfig { Percentage = 90 }
        };

        _configResolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Returns(config);

        _budgetManagerMock.Setup(x => x.CalculateBreakdown(It.IsAny<SessionData>(), It.IsAny<string?>(), It.IsAny<int?>()))
            .Returns(new TokenBreakdown());

        _budgetManagerMock.Setup(x => x.CheckBudget(It.IsAny<SessionData>(), It.IsAny<TokenBudgetConfig>(), It.IsAny<int>()))
            .Returns(new BudgetStatus { Level = BudgetLevel.Warning, CurrentTokens = 850, MaxTokens = 1000 });

        var payload = HookPayload.FireAndForget(
            HookRegistry.ChatAfterComplete,
            "test-session",
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent
            });

        var hook = new BudgetUpdateHook(_budgetManagerMock.Object, _configResolverMock.Object, _tokenCounterMock.Object);

        // Act
        var result = await hook.ExecuteAsync(payload);

        // Assert
        result.Continue.Should().BeTrue();
        session.PendingCompaction.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WhenOverflow_SetsPendingCompaction()
    {
        // Arrange
        var session = new SessionData();
        var agent = new AgentDefinition();

        var config = new TokenBudgetConfig { MaxContextTokens = 1000 };

        _configResolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Returns(config);

        _budgetManagerMock.Setup(x => x.CalculateBreakdown(It.IsAny<SessionData>(), It.IsAny<string?>(), It.IsAny<int?>()))
            .Returns(new TokenBreakdown());

        _budgetManagerMock.Setup(x => x.CheckBudget(It.IsAny<SessionData>(), It.IsAny<TokenBudgetConfig>(), It.IsAny<int>()))
            .Returns(new BudgetStatus { Level = BudgetLevel.Overflow, CurrentTokens = 1100, MaxTokens = 1000 });

        var payload = HookPayload.FireAndForget(
            HookRegistry.ChatAfterComplete,
            "test-session",
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent
            });

        var hook = new BudgetUpdateHook(_budgetManagerMock.Object, _configResolverMock.Object, _tokenCounterMock.Object);

        // Act
        var result = await hook.ExecuteAsync(payload);

        // Assert
        result.Continue.Should().BeTrue();
        session.PendingCompaction.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_StoresBudgetStatusEventInMutable()
    {
        // Arrange
        var session = new SessionData();
        var agent = new AgentDefinition();

        var config = new TokenBudgetConfig { MaxContextTokens = 1000 };

        var breakdown = new TokenBreakdown
        {
            BySource = new SourceBreakdown
            {
                SystemPrompt = 100,
                UserMessages = 200,
                AssistantMessages = 150
            },
            ByRole = new RoleBreakdown
            {
                System = 100,
                User = 200,
                Assistant = 150
            }
        };

        _configResolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Returns(config);

        _budgetManagerMock.Setup(x => x.CalculateBreakdown(It.IsAny<SessionData>(), It.IsAny<string?>(), It.IsAny<int?>()))
            .Returns(breakdown);

        _budgetManagerMock.Setup(x => x.CheckBudget(It.IsAny<SessionData>(), It.IsAny<TokenBudgetConfig>(), It.IsAny<int>()))
            .Returns(new BudgetStatus { Level = BudgetLevel.Normal, CurrentTokens = 450, MaxTokens = 1000 });

        var payload = HookPayload.FireAndForget(
            HookRegistry.ChatAfterComplete,
            "test-session",
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent
            });

        var hook = new BudgetUpdateHook(_budgetManagerMock.Object, _configResolverMock.Object, _tokenCounterMock.Object);

        // Act
        await hook.ExecuteAsync(payload);

        // Assert
        payload.Mutable.Should().ContainKey("budgetStatusEvent");
        var budgetEvent = payload.Mutable["budgetStatusEvent"] as BudgetStatusEvent;
        budgetEvent.Should().NotBeNull();
        budgetEvent!.SessionId.Should().Be("test-session");
        budgetEvent.CurrentTokens.Should().Be(450);
        budgetEvent.MaxTokens.Should().Be(1000);
        budgetEvent.Level.Should().Be(BudgetLevel.Normal);
        budgetEvent.Breakdown.Should().NotBeNull();
        budgetEvent.Breakdown!.TotalTokens.Should().Be(450);
    }

    [Fact]
    public async Task ExecuteAsync_WhenWarningLevel_StoresBudgetWarningEventInMutable()
    {
        // Arrange
        var session = new SessionData();
        var agent = new AgentDefinition();

        var config = new TokenBudgetConfig
        {
            MaxContextTokens = 1000,
            WarningThreshold = new ThresholdConfig { Percentage = 80 }
        };

        _configResolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Returns(config);

        _budgetManagerMock.Setup(x => x.CalculateBreakdown(It.IsAny<SessionData>(), It.IsAny<string?>(), It.IsAny<int?>()))
            .Returns(new TokenBreakdown());

        _budgetManagerMock.Setup(x => x.CheckBudget(It.IsAny<SessionData>(), It.IsAny<TokenBudgetConfig>(), It.IsAny<int>()))
            .Returns(new BudgetStatus { Level = BudgetLevel.Warning, CurrentTokens = 850, MaxTokens = 1000 });

        var payload = HookPayload.FireAndForget(
            HookRegistry.ChatAfterComplete,
            "test-session",
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent
            });

        var hook = new BudgetUpdateHook(_budgetManagerMock.Object, _configResolverMock.Object, _tokenCounterMock.Object);

        // Act
        await hook.ExecuteAsync(payload);

        // Assert
        payload.Mutable.Should().ContainKey("budgetWarningEvent");
        var warningEvent = payload.Mutable["budgetWarningEvent"] as BudgetWarningEvent;
        warningEvent.Should().NotBeNull();
        warningEvent!.SessionId.Should().Be("test-session");
        warningEvent.Level.Should().Be(BudgetLevel.Warning);
        warningEvent.Message.Should().Contain("approaching limit");
    }

    [Fact]
    public async Task ExecuteAsync_WhenCriticalLevel_StoresWarningEventWithCompactionMessage()
    {
        // Arrange
        var session = new SessionData();
        var agent = new AgentDefinition();

        var config = new TokenBudgetConfig { MaxContextTokens = 1000 };

        _configResolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Returns(config);

        _budgetManagerMock.Setup(x => x.CalculateBreakdown(It.IsAny<SessionData>(), It.IsAny<string?>(), It.IsAny<int?>()))
            .Returns(new TokenBreakdown());

        _budgetManagerMock.Setup(x => x.CheckBudget(It.IsAny<SessionData>(), It.IsAny<TokenBudgetConfig>(), It.IsAny<int>()))
            .Returns(new BudgetStatus { Level = BudgetLevel.Critical, CurrentTokens = 950, MaxTokens = 1000 });

        var payload = HookPayload.FireAndForget(
            HookRegistry.ChatAfterComplete,
            "test-session",
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent
            });

        var hook = new BudgetUpdateHook(_budgetManagerMock.Object, _configResolverMock.Object, _tokenCounterMock.Object);

        // Act
        await hook.ExecuteAsync(payload);

        // Assert
        payload.Mutable.Should().ContainKey("budgetWarningEvent");
        var warningEvent = payload.Mutable["budgetWarningEvent"] as BudgetWarningEvent;
        warningEvent.Should().NotBeNull();
        warningEvent!.Level.Should().Be(BudgetLevel.Critical);
        warningEvent.Message.Should().Contain("compaction recommended");
    }

    [Fact]
    public async Task ExecuteAsync_WhenNormal_DoesNotStoreWarningEvent()
    {
        // Arrange
        var session = new SessionData();
        var agent = new AgentDefinition();

        var config = new TokenBudgetConfig { MaxContextTokens = 1000 };

        _configResolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Returns(config);

        _budgetManagerMock.Setup(x => x.CalculateBreakdown(It.IsAny<SessionData>(), It.IsAny<string?>(), It.IsAny<int?>()))
            .Returns(new TokenBreakdown());

        _budgetManagerMock.Setup(x => x.CheckBudget(It.IsAny<SessionData>(), It.IsAny<TokenBudgetConfig>(), It.IsAny<int>()))
            .Returns(new BudgetStatus { Level = BudgetLevel.Normal, CurrentTokens = 100, MaxTokens = 1000 });

        var payload = HookPayload.FireAndForget(
            HookRegistry.ChatAfterComplete,
            "test-session",
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent
            });

        var hook = new BudgetUpdateHook(_budgetManagerMock.Object, _configResolverMock.Object, _tokenCounterMock.Object);

        // Act
        await hook.ExecuteAsync(payload);

        // Assert
        payload.Mutable.Should().NotContainKey("budgetWarningEvent");
    }

    [Fact]
    public async Task ExecuteAsync_WhenCompactionResultInMutable_StoresCompactionEvent()
    {
        // Arrange
        var session = new SessionData();
        var agent = new AgentDefinition();

        var config = new TokenBudgetConfig
        {
            MaxContextTokens = 1000,
            CompactionStrategy = CompactionStrategyType.SlidingWindow
        };

        _configResolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Returns(config);

        _budgetManagerMock.Setup(x => x.CalculateBreakdown(It.IsAny<SessionData>(), It.IsAny<string?>(), It.IsAny<int?>()))
            .Returns(new TokenBreakdown());

        _budgetManagerMock.Setup(x => x.CheckBudget(It.IsAny<SessionData>(), It.IsAny<TokenBudgetConfig>(), It.IsAny<int>()))
            .Returns(new BudgetStatus { Level = BudgetLevel.Normal, CurrentTokens = 500, MaxTokens = 1000 });

        var payload = HookPayload.FireAndForget(
            HookRegistry.ChatAfterComplete,
            "test-session",
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent
            });

        // Pre-populate Mutable with compaction result (simulating what BudgetCheckHook would have done)
        payload.Mutable["compactionResult"] = SessionCompressionResult.Succeeded(2000, 800, 5, new List<SessionMessage>());

        var hook = new BudgetUpdateHook(_budgetManagerMock.Object, _configResolverMock.Object, _tokenCounterMock.Object);

        // Act
        await hook.ExecuteAsync(payload);

        // Assert
        payload.Mutable.Should().ContainKey("compactionEvent");
        var compactionEvent = payload.Mutable["compactionEvent"] as CompactionEvent;
        compactionEvent.Should().NotBeNull();
        compactionEvent!.SessionId.Should().Be("test-session");
        compactionEvent.Strategy.Should().Be("SlidingWindow");
        compactionEvent.TokensBefore.Should().Be(2000);
        compactionEvent.TokensAfter.Should().Be(800);
        compactionEvent.MessagesRemoved.Should().Be(5);
        compactionEvent.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WhenSessionIsNull_ReturnsSuccess()
    {
        // Arrange
        var agent = new AgentDefinition();
        var payload = HookPayload.FireAndForget(
            HookRegistry.ChatAfterComplete,
            "test-session",
            new Dictionary<string, object?>
            {
                ["session"] = null!,
                ["agent"] = agent
            });

        var hook = new BudgetUpdateHook(_budgetManagerMock.Object, _configResolverMock.Object, _tokenCounterMock.Object);

        // Act
        var result = await hook.ExecuteAsync(payload);

        // Assert
        result.Continue.Should().BeTrue();
        _budgetManagerMock.Verify(x => x.CalculateBreakdown(
            It.IsAny<SessionData>(),
            It.IsAny<string?>(),
            It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAgentIsNull_ReturnsSuccess()
    {
        // Arrange
        var session = new SessionData();
        var payload = HookPayload.FireAndForget(
            HookRegistry.ChatAfterComplete,
            "test-session",
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = null!
            });

        var hook = new BudgetUpdateHook(_budgetManagerMock.Object, _configResolverMock.Object, _tokenCounterMock.Object);

        // Act
        var result = await hook.ExecuteAsync(payload);

        // Assert
        result.Continue.Should().BeTrue();
        _budgetManagerMock.Verify(x => x.CalculateBreakdown(
            It.IsAny<SessionData>(),
            It.IsAny<string?>(),
            It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenException_DoesNotBlock()
    {
        // Arrange
        var session = new SessionData();
        var agent = new AgentDefinition();

        _configResolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Throws(new InvalidOperationException("Test exception"));

        var payload = HookPayload.FireAndForget(
            HookRegistry.ChatAfterComplete,
            "test-session",
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent
            });

        var hook = new BudgetUpdateHook(_budgetManagerMock.Object, _configResolverMock.Object, _tokenCounterMock.Object);

        // Act
        var result = await hook.ExecuteAsync(payload);

        // Assert
        result.Continue.Should().BeTrue();
    }

    [Fact]
    public void Hook_ShouldHaveCorrectSpec()
    {
        // Arrange
        var hook = new BudgetUpdateHook(_budgetManagerMock.Object, _configResolverMock.Object, _tokenCounterMock.Object);

        // Assert
        hook.Spec.Should().Be(HookRegistry.ChatAfterComplete);
    }

    [Fact]
    public void Hook_ShouldHaveHighPriority()
    {
        // Arrange
        var hook = new BudgetUpdateHook(_budgetManagerMock.Object, _configResolverMock.Object, _tokenCounterMock.Object);

        // Assert
        hook.Priority.Should().Be(100);
    }

    [Fact]
    public async Task ExecuteAsync_PassesCorrectConfigsToResolver()
    {
        // Arrange
        var sessionConfig = new TokenBudgetConfig { MaxContextTokens = 50000 };
        var agentConfig = new TokenBudgetConfig { MaxContextTokens = 80000 };
        var session = new SessionData { BudgetConfig = sessionConfig };
        var agent = new AgentDefinition { BudgetConfig = agentConfig };
        var payload = HookPayload.FireAndForget(
            HookRegistry.ChatAfterComplete,
            "test-session",
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent
            });

        TokenBudgetConfig? capturedSessionConfig = null;
        TokenBudgetConfig? capturedAgentConfig = null;

        _configResolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Callback<TokenBudgetConfig?, TokenBudgetConfig?, TokenBudgetConfig?>((s, a, g) =>
            {
                capturedSessionConfig = s;
                capturedAgentConfig = a;
            })
            .Returns(new TokenBudgetConfig());

        _budgetManagerMock.Setup(x => x.CalculateBreakdown(It.IsAny<SessionData>(), It.IsAny<string?>(), It.IsAny<int?>()))
            .Returns(new TokenBreakdown());

        _budgetManagerMock.Setup(x => x.CheckBudget(It.IsAny<SessionData>(), It.IsAny<TokenBudgetConfig>(), It.IsAny<int>()))
            .Returns(new BudgetStatus());

        var hook = new BudgetUpdateHook(_budgetManagerMock.Object, _configResolverMock.Object, _tokenCounterMock.Object);

        // Act
        await hook.ExecuteAsync(payload);

        // Assert
        capturedSessionConfig.Should().Be(sessionConfig);
        capturedAgentConfig.Should().Be(agentConfig);
    }

    [Fact]
    public async Task ExecuteAsync_ExtractsOptionalInputs()
    {
        // Arrange
        var session = new SessionData();
        var agent = new AgentDefinition();
        var systemPrompt = "You are a helpful assistant";
        var toolTokens = 500;

        _configResolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Returns(new TokenBudgetConfig());

        TokenBreakdown? capturedBreakdown = null;
        _budgetManagerMock.Setup(x => x.CalculateBreakdown(It.IsAny<SessionData>(), It.IsAny<string?>(), It.IsAny<int?>()))
            .Callback<SessionData, string?, int?>((s, sp, tt) =>
            {
                capturedBreakdown = new TokenBreakdown();
            })
            .Returns(new TokenBreakdown());

        _budgetManagerMock.Setup(x => x.CheckBudget(It.IsAny<SessionData>(), It.IsAny<TokenBudgetConfig>(), It.IsAny<int>()))
            .Returns(new BudgetStatus());

        var payload = HookPayload.FireAndForget(
            HookRegistry.ChatAfterComplete,
            "test-session",
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent,
                ["systemPrompt"] = systemPrompt,
                ["toolTokens"] = toolTokens
            });

        var hook = new BudgetUpdateHook(_budgetManagerMock.Object, _configResolverMock.Object, _tokenCounterMock.Object);

        // Act
        await hook.ExecuteAsync(payload);

        // Assert
        _budgetManagerMock.Verify(x => x.CalculateBreakdown(
            It.IsAny<SessionData>(),
            systemPrompt,
            toolTokens), Times.Once);
    }
}

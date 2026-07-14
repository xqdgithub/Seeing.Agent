using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Models;
using Seeing.Agent.TokenBudget;
using Seeing.Session.Compression;
using Seeing.Session.Compression.Strategies;
using Seeing.Session.Core;
using Seeing.Session.Storage;
using Seeing.TokenBudget.Api.Responses;
using Seeing.TokenBudget.Configuration;
using Seeing.TokenBudget.Extensions;
using Seeing.TokenEstimation;
using Xunit;

// Alias to disambiguate from Seeing.TokenBudget.CompressionResult
using SessionCompressionResult = Seeing.Session.Core.CompressionResult;

namespace Seeing.TokenBudget.Tests.Integration;

/// <summary>
/// Test-only compression strategy factory that only provides SlidingWindow strategy.
/// This avoids the need for ISummarizer dependency in tests.
/// </summary>
public class TestCompressionStrategyFactory : ICompressionStrategyFactory
{
    private readonly SlidingWindowTokenStrategy _slidingWindowStrategy;

    public TestCompressionStrategyFactory(SlidingWindowTokenStrategy slidingWindowStrategy)
    {
        _slidingWindowStrategy = slidingWindowStrategy;
    }

    public ICompressionStrategy GetStrategy(CompactionStrategyType type)
    {
        // For tests, always use SlidingWindow regardless of requested type
        return _slidingWindowStrategy;
    }
}

/// <summary>
/// End-to-end integration tests for token budget management.
/// Tests the complete flow from message addition through compression.
/// </summary>
public class EndToEndIntegrationTests : IAsyncLifetime
{
    private ServiceProvider _services = null!;
    private IHookManager _hookManager = null!;
    private ITokenBudgetManager _budgetManager = null!;
    private ITokenCounter _tokenCounter = null!;
    private ICompressionService _compressionService = null!;
    private ITokenBudgetConfigResolver _configResolver = null!;
    private ISessionStore _sessionStore = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();

        // Add logging
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));

        // Add configuration with low token budget for testing
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TokenBudget:DefaultConfig:MaxContextTokens"] = "1000",
                ["TokenBudget:DefaultConfig:SlidingWindowKeepTokens"] = "500"
            })
            .Build();

        // Add session store
        services.AddSingleton<ISessionStore, InMemorySessionStore>();

        // Add base token budget services (without Summarizing/Hybrid strategies that need ISummarizer)
        services.AddTokenBudgetManagement(config);

        // Add only SlidingWindow strategy for tests (does not need LLM)
        services.AddSingleton<SlidingWindowTokenStrategy>();
        services.AddSingleton<ICompressionStrategyFactory, TestCompressionStrategyFactory>();

        // Add compression service
        services.AddScoped<ICompressionService, CompressionService>();

        // Add hook infrastructure
        services.AddSingleton<IHookManager, HookManager>();

        // Add token budget hooks
        services.AddSingleton<BudgetCheckHook>();
        services.AddSingleton<BudgetUpdateHook>();

        _services = services.BuildServiceProvider();

        // Register hooks manually
        var hookManager = _services.GetRequiredService<IHookManager>();
        var checkHook = _services.GetRequiredService<BudgetCheckHook>();
        var updateHook = _services.GetRequiredService<BudgetUpdateHook>();
        hookManager.Register(checkHook);
        hookManager.Register(updateHook);

        // Resolve services
        _hookManager = _services.GetRequiredService<IHookManager>();
        _budgetManager = _services.GetRequiredService<ITokenBudgetManager>();
        _tokenCounter = _services.GetRequiredService<ITokenCounter>();
        _compressionService = _services.GetRequiredService<ICompressionService>();
        _configResolver = _services.GetRequiredService<ITokenBudgetConfigResolver>();
        _sessionStore = _services.GetRequiredService<ISessionStore>();

        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
    }

    [Fact]
    public async Task FullFlow_AddMessagesAndTriggerCompression()
    {
        // Arrange
        var session = SessionData.Create();
        session.BudgetConfig = new TokenBudgetConfig
        {
            MaxContextTokens = 1000,
            WarningThreshold = new ThresholdConfig { Percentage = 80 },
            CompactionThreshold = new ThresholdConfig { Percentage = 90 },
            SlidingWindowKeepTokens = 500,
            AutoCompactionEnabled = true
        };

        var agent = new AgentDefinition
        {
            Name = "test-agent",
            BudgetConfig = null // Use session config
        };

        // Add a system message
        session.Messages.Add(SessionMessage.SystemMessage("You are a helpful assistant. " + new string('x', 100)));

        // Act - Add messages that exceed the budget
        for (int i = 0; i < 20; i++)
        {
            session.Messages.Add(SessionMessage.UserMessage($"User message {i} with some content to increase token count. " + new string('y', 50)));
            session.Messages.Add(SessionMessage.AssistantMessage($"Assistant response {i} with more content. " + new string('z', 50)));
        }

        // Calculate initial breakdown
        var initialBreakdown = _budgetManager.CalculateBreakdown(session);

        // Assert - Token counting works
        initialBreakdown.Total.Should().BeGreaterThan(0, "Should count tokens from messages");
        initialBreakdown.BySource.UserMessages.Should().BeGreaterThan(0);
        initialBreakdown.BySource.AssistantMessages.Should().BeGreaterThan(0);

        // Check budget status - should be at critical or overflow level
        var config = _configResolver.Resolve(session.BudgetConfig, agent.BudgetConfig, null);
        var initialStatus = _budgetManager.CheckBudget(session, config, initialBreakdown.Total);
        initialStatus.Level.Should().BeOneOf(BudgetLevel.Critical, BudgetLevel.Overflow);

        // Manually set PendingCompaction (simulating what BudgetUpdateHook would do)
        session.PendingCompaction = true;

        // Simulate chat.before_start hook (BudgetCheckHook) - should trigger compression
        var checkPayload = HookPayload.Blocking(
            HookRegistry.ChatBeforeStart,
            session.Id,
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent
            });

        var checkResult = await _hookManager.TriggerAsync(checkPayload);

        // Assert - Compression should have occurred
        checkResult.Continue.Should().BeTrue();
        session.PendingCompaction.Should().BeFalse("Hook should clear the flag after compression");

        // Verify compression result
        checkPayload.Mutable.Should().ContainKey("compactionResult");
        var compactionResultObj = checkPayload.Mutable["compactionResult"];
        compactionResultObj.Should().NotBeNull($"but was of type {compactionResultObj?.GetType().FullName ?? "null"}");
        var compactionResult = compactionResultObj as SessionCompressionResult;
        compactionResult.Should().NotBeNull();
        compactionResult!.Success.Should().BeTrue();
        compactionResult.TokensBefore.Should().BeGreaterThan(0);
        compactionResult.TokensAfter.Should().BeLessThan(compactionResult.TokensBefore, "Compression should reduce tokens");
        compactionResult.MessagesRemoved.Should().BeGreaterThan(0, "Should have removed some messages");

        // Verify compression reduced token count
        var compressedBreakdown = _budgetManager.CalculateBreakdown(session);
        compressedBreakdown.Total.Should().BeLessThan(initialBreakdown.Total, "Token count should decrease after compression");
    }

    [Fact]
    public async Task WarningThreshold_CalculatesCorrectly()
    {
        // Arrange
        var session = SessionData.Create();
        session.BudgetConfig = new TokenBudgetConfig
        {
            MaxContextTokens = 500, // Lower budget to ensure we hit warning
            WarningThreshold = new ThresholdConfig { Percentage = 50 },
            CompactionThreshold = new ThresholdConfig { Percentage = 90 }
        };

        // Add messages to reach ~50% of budget (250 tokens)
        for (int i = 0; i < 15; i++)
        {
            session.Messages.Add(SessionMessage.UserMessage($"User message {i} " + new string('a', 40)));
            session.Messages.Add(SessionMessage.AssistantMessage($"Assistant response {i} " + new string('b', 40)));
        }

        // Act
        var breakdown = _budgetManager.CalculateBreakdown(session);
        var status = _budgetManager.CheckBudget(session, session.BudgetConfig!, breakdown.Total);

        // Assert - Should be at warning level or higher
        status.UsagePercentage.Should().BeGreaterThanOrEqualTo(50);
    }

    [Fact]
    public async Task CriticalThreshold_CalculatedCorrectly()
    {
        // Arrange
        var session = SessionData.Create();
        session.BudgetConfig = new TokenBudgetConfig
        {
            MaxContextTokens = 500,
            WarningThreshold = new ThresholdConfig { Percentage = 50 },
            CompactionThreshold = new ThresholdConfig { Percentage = 80 },
            SlidingWindowKeepTokens = 200
        };

        // Add many messages to exceed critical threshold
        session.Messages.Add(SessionMessage.SystemMessage("System prompt with enough content"));
        for (int i = 0; i < 15; i++)
        {
            session.Messages.Add(SessionMessage.UserMessage($"User message {i} with more content to fill budget " + new string('c', 20)));
            session.Messages.Add(SessionMessage.AssistantMessage($"Assistant response {i} with content " + new string('d', 20)));
        }

        // Act
        var breakdown = _budgetManager.CalculateBreakdown(session);
        var status = _budgetManager.CheckBudget(session, session.BudgetConfig!, breakdown.Total);

        // Assert - Should be at critical or overflow level
        status.Level.Should().BeOneOf(BudgetLevel.Critical, BudgetLevel.Overflow);
    }

    [Fact]
    public async Task BudgetCheckHook_TriggersCompression_WhenPendingCompactionSet()
    {
        // Arrange
        var session = SessionData.Create();
        session.BudgetConfig = new TokenBudgetConfig
        {
            MaxContextTokens = 500,
            SlidingWindowKeepTokens = 200,
            AutoCompactionEnabled = true
        };
        session.PendingCompaction = true;

        // Add messages
        session.Messages.Add(SessionMessage.SystemMessage("System prompt"));
        for (int i = 0; i < 10; i++)
        {
            session.Messages.Add(SessionMessage.UserMessage($"User message {i} " + new string('e', 25)));
            session.Messages.Add(SessionMessage.AssistantMessage($"Assistant response {i} " + new string('f', 25)));
        }

        var initialTokenCount = _budgetManager.CalculateBreakdown(session).Total;
        var agent = new AgentDefinition { Name = "test-agent" };

        // Act - Trigger the before_start hook (Blocking policy - waits for completion)
        var payload = HookPayload.Blocking(
            HookRegistry.ChatBeforeStart,
            session.Id,
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent
            });

        var result = await _hookManager.TriggerAsync(payload);

        // Assert
        result.Continue.Should().BeTrue();
        session.PendingCompaction.Should().BeFalse("Flag should be cleared after compression");

        // Verify compression result stored in mutable
        payload.Mutable.Should().ContainKey("compactionResult");
        var compactionResult = payload.Mutable["compactionResult"] as SessionCompressionResult;
        compactionResult.Should().NotBeNull();
        compactionResult!.Success.Should().BeTrue();
        compactionResult.TokensBefore.Should().BeGreaterThan(0);
        compactionResult.MessagesRemoved.Should().BeGreaterThan(0);

        // Verify messages were actually compressed
        var finalTokenCount = _budgetManager.CalculateBreakdown(session).Total;
        finalTokenCount.Should().BeLessThan(initialTokenCount);
    }

    [Fact]
    public async Task Compression_ReducesTokenCount_BelowTarget()
    {
        // Arrange
        var session = SessionData.Create();
        session.BudgetConfig = new TokenBudgetConfig
        {
            MaxContextTokens = 1000,
            SlidingWindowKeepTokens = 300, // Target to keep after compression
            AutoCompactionEnabled = true,
            CompactionStrategy = CompactionStrategyType.SlidingWindow
        };

        // Add a system message
        session.Messages.Add(SessionMessage.SystemMessage("You are a helpful assistant. " + new string('x', 100)));

        // Add many messages to exceed budget significantly
        for (int i = 0; i < 30; i++)
        {
            session.Messages.Add(SessionMessage.UserMessage($"User message {i} with content to increase token count significantly. " + new string('y', 30)));
            session.Messages.Add(SessionMessage.AssistantMessage($"Assistant response {i} with detailed content. " + new string('z', 30)));
        }

        var initialBreakdown = _budgetManager.CalculateBreakdown(session);
        var agent = new AgentDefinition { Name = "test-agent" };

        // Act - Execute compression directly
        var result = await _compressionService.CompressAsync(session, session.BudgetConfig, agent.BudgetConfig);

        // Assert
        result.Success.Should().BeTrue();
        result.TokensBefore.Should().Be(initialBreakdown.Total);
        result.TokensAfter.Should().BeLessThan(result.TokensBefore);
        result.TokensAfter.Should().BeLessThanOrEqualTo(session.BudgetConfig!.SlidingWindowKeepTokens + 100, "Should be close to target");

        // Verify session messages were updated
        var compressedBreakdown = _budgetManager.CalculateBreakdown(session);
        compressedBreakdown.Total.Should().BeLessThan(initialBreakdown.Total);
    }

    [Fact]
    public async Task AutoCompactionDisabled_DoesNotCompress()
    {
        // Arrange
        var session = SessionData.Create();
        session.BudgetConfig = new TokenBudgetConfig
        {
            MaxContextTokens = 500,
            SlidingWindowKeepTokens = 200,
            AutoCompactionEnabled = false // Disabled
        };
        session.PendingCompaction = true;

        // Add messages
        session.Messages.Add(SessionMessage.SystemMessage("System prompt"));
        for (int i = 0; i < 10; i++)
        {
            session.Messages.Add(SessionMessage.UserMessage($"User message {i} " + new string('g', 25)));
            session.Messages.Add(SessionMessage.AssistantMessage($"Assistant response {i} " + new string('h', 25)));
        }

        var initialMessageCount = session.Messages.Count;
        var agent = new AgentDefinition { Name = "test-agent" };

        // Act
        var payload = HookPayload.Blocking(
            HookRegistry.ChatBeforeStart,
            session.Id,
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent
            });

        var result = await _hookManager.TriggerAsync(payload);

        // Assert
        result.Continue.Should().BeTrue();
        session.PendingCompaction.Should().BeFalse("Flag should be cleared even when disabled");
        session.Messages.Count.Should().Be(initialMessageCount, "Messages should not be modified when auto-compaction is disabled");
    }

    [Fact]
    public async Task CompactionEvent_CreatedCorrectly_WhenCompressionOccurs()
    {
        // Arrange
        var session = SessionData.Create();
        session.BudgetConfig = new TokenBudgetConfig
        {
            MaxContextTokens = 500,
            SlidingWindowKeepTokens = 200,
            AutoCompactionEnabled = true,
            CompactionStrategy = CompactionStrategyType.SlidingWindow
        };
        session.PendingCompaction = true;

        session.Messages.Add(SessionMessage.SystemMessage("System prompt"));
        for (int i = 0; i < 10; i++)
        {
            session.Messages.Add(SessionMessage.UserMessage($"User message {i} " + new string('i', 25)));
            session.Messages.Add(SessionMessage.AssistantMessage($"Assistant response {i} " + new string('j', 25)));
        }

        var agent = new AgentDefinition { Name = "test-agent" };

        // First, run the before_start hook to get compaction result
        var checkPayload = HookPayload.Blocking(
            HookRegistry.ChatBeforeStart,
            session.Id,
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent
            });

        await _hookManager.TriggerAsync(checkPayload);

        // Now simulate BudgetUpdateHook processing the compaction result
        var completePayload = HookPayload.FireAndForget(
            HookRegistry.ChatAfterComplete,
            session.Id,
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent
            });

        // Copy the compaction result from check payload
        if (checkPayload.Mutable.TryGetValue("compactionResult", out var compactionResult))
        {
            completePayload.Mutable["compactionResult"] = compactionResult;
        }

        // Manually invoke BudgetUpdateHook logic (since FireAndForget runs in background)
        var updateHook = _services.GetRequiredService<BudgetUpdateHook>();
        await updateHook.ExecuteAsync(completePayload);

        // Assert - Compaction event should be present
        completePayload.Mutable.Should().ContainKey("compactionEvent");
        var compactionEvent = completePayload.Mutable["compactionEvent"] as CompactionEvent;
        compactionEvent.Should().NotBeNull();
        compactionEvent!.SessionId.Should().Be(session.Id);
        compactionEvent.Strategy.Should().Be("SlidingWindow");
        compactionEvent.Success.Should().BeTrue();
        compactionEvent.TokensBefore.Should().BeGreaterThan(0);
        compactionEvent.TokensAfter.Should().BeLessThan(compactionEvent.TokensBefore);
    }

    [Fact]
    public async Task EmptySession_DoesNotCauseErrors()
    {
        // Arrange
        var session = SessionData.Create();
        session.BudgetConfig = new TokenBudgetConfig { MaxContextTokens = 1000 };
        var agent = new AgentDefinition { Name = "test-agent" };

        // Act - Run the before_start hook on empty session
        var checkPayload = HookPayload.Blocking(
            HookRegistry.ChatBeforeStart,
            session.Id,
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent
            });

        var checkResult = await _hookManager.TriggerAsync(checkPayload);

        // Now manually run BudgetUpdateHook
        var completePayload = HookPayload.FireAndForget(
            HookRegistry.ChatAfterComplete,
            session.Id,
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent
            });

        var updateHook = _services.GetRequiredService<BudgetUpdateHook>();
        var completeResult = await updateHook.ExecuteAsync(completePayload);

        // Assert
        checkResult.Continue.Should().BeTrue();
        completeResult.Continue.Should().BeTrue();
        session.PendingCompaction.Should().BeFalse();

        // Budget status should be normal with 0 tokens
        completePayload.Mutable.Should().ContainKey("budgetStatusEvent");
        var budgetEvent = completePayload.Mutable["budgetStatusEvent"] as BudgetStatusEvent;
        budgetEvent.Should().NotBeNull();
        budgetEvent!.CurrentTokens.Should().Be(0);
        budgetEvent.Level.Should().Be(BudgetLevel.Normal);
    }

    [Fact]
    public async Task ConfigResolver_UsesCorrectPriority()
    {
        // Arrange
        var globalConfig = new TokenBudgetConfig { MaxContextTokens = 100000 };
        var agentConfig = new TokenBudgetConfig { MaxContextTokens = 80000 };
        var sessionConfig = new TokenBudgetConfig { MaxContextTokens = 50000 };

        var session = SessionData.Create();
        session.BudgetConfig = sessionConfig;

        var agent = new AgentDefinition { Name = "test-agent", BudgetConfig = agentConfig };

        // Act - Test with explicit configs
        var result = _configResolver.Resolve(sessionConfig, agentConfig, globalConfig);

        // Assert - Session config should have highest priority
        result.MaxContextTokens.Should().Be(50000);

        // Test with null session config (agent should win)
        var resultWithNullSession = _configResolver.Resolve(null, agentConfig, globalConfig);
        resultWithNullSession.MaxContextTokens.Should().Be(80000);

        // Test with both null (global should win)
        var resultWithNulls = _configResolver.Resolve(null, null, globalConfig);
        resultWithNulls.MaxContextTokens.Should().Be(100000);
    }

    [Fact]
    public async Task BudgetStatusEvent_CreatedCorrectly()
    {
        // Arrange
        var session = SessionData.Create();
        session.BudgetConfig = new TokenBudgetConfig
        {
            MaxContextTokens = 1000,
            WarningThreshold = new ThresholdConfig { Percentage = 80 },
            CompactionThreshold = new ThresholdConfig { Percentage = 90 }
        };

        var agent = new AgentDefinition { Name = "test-agent" };

        // Add messages
        session.Messages.Add(SessionMessage.SystemMessage("System prompt"));
        for (int i = 0; i < 10; i++)
        {
            session.Messages.Add(SessionMessage.UserMessage($"User message {i} " + new string('a', 30)));
            session.Messages.Add(SessionMessage.AssistantMessage($"Assistant response {i} " + new string('b', 30)));
        }

        // Act - Manually run BudgetUpdateHook
        var payload = HookPayload.FireAndForget(
            HookRegistry.ChatAfterComplete,
            session.Id,
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent
            });

        var updateHook = _services.GetRequiredService<BudgetUpdateHook>();
        await updateHook.ExecuteAsync(payload);

        // Assert
        payload.Mutable.Should().ContainKey("budgetStatusEvent");
        var budgetEvent = payload.Mutable["budgetStatusEvent"] as BudgetStatusEvent;
        budgetEvent.Should().NotBeNull();
        budgetEvent!.SessionId.Should().Be(session.Id);
        budgetEvent.MaxTokens.Should().Be(1000);
        budgetEvent.Breakdown.Should().NotBeNull();
        budgetEvent.Breakdown!.TotalTokens.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task WarningEvent_CreatedWhenOverWarningThreshold()
    {
        // Arrange
        var session = SessionData.Create();
        session.BudgetConfig = new TokenBudgetConfig
        {
            MaxContextTokens = 500, // Lower to ensure we hit warning
            WarningThreshold = new ThresholdConfig { Percentage = 50 },
            CompactionThreshold = new ThresholdConfig { Percentage = 90 }
        };

        var agent = new AgentDefinition { Name = "test-agent" };

        // Add enough messages to exceed warning threshold
        session.Messages.Add(SessionMessage.SystemMessage("System prompt"));
        for (int i = 0; i < 15; i++)
        {
            session.Messages.Add(SessionMessage.UserMessage($"User message {i} " + new string('a', 30)));
            session.Messages.Add(SessionMessage.AssistantMessage($"Assistant response {i} " + new string('b', 30)));
        }

        // Verify we're at warning or higher
        var breakdown = _budgetManager.CalculateBreakdown(session);
        var status = _budgetManager.CheckBudget(session, session.BudgetConfig!, breakdown.Total);
        status.Level.Should().BeOneOf(BudgetLevel.Warning, BudgetLevel.Critical, BudgetLevel.Overflow);

        // Act - Manually run BudgetUpdateHook
        var payload = HookPayload.FireAndForget(
            HookRegistry.ChatAfterComplete,
            session.Id,
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent
            });

        var updateHook = _services.GetRequiredService<BudgetUpdateHook>();
        await updateHook.ExecuteAsync(payload);

        // Assert - Warning event should be created
        payload.Mutable.Should().ContainKey("budgetWarningEvent");
        var warningEvent = payload.Mutable["budgetWarningEvent"] as BudgetWarningEvent;
        warningEvent.Should().NotBeNull();
        warningEvent!.SessionId.Should().Be(session.Id);
        warningEvent.Level.Should().BeOneOf(BudgetLevel.Warning, BudgetLevel.Critical, BudgetLevel.Overflow);
    }

    [Fact]
    public async Task PendingCompaction_SetWhenOverCriticalThreshold()
    {
        // Arrange
        var session = SessionData.Create();
        session.BudgetConfig = new TokenBudgetConfig
        {
            MaxContextTokens = 400, // Very low to force overflow
            WarningThreshold = new ThresholdConfig { Percentage = 50 },
            CompactionThreshold = new ThresholdConfig { Percentage = 80 }
        };

        var agent = new AgentDefinition { Name = "test-agent" };

        // Add many messages
        for (int i = 0; i < 20; i++)
        {
            session.Messages.Add(SessionMessage.UserMessage($"User message {i} with more content " + new string('a', 30)));
            session.Messages.Add(SessionMessage.AssistantMessage($"Assistant response {i} with content " + new string('b', 30)));
        }

        // Act - Manually run BudgetUpdateHook
        var payload = HookPayload.FireAndForget(
            HookRegistry.ChatAfterComplete,
            session.Id,
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent
            });

        var updateHook = _services.GetRequiredService<BudgetUpdateHook>();
        await updateHook.ExecuteAsync(payload);

        // Assert - PendingCompaction should be set for Critical or Overflow
        var breakdown = _budgetManager.CalculateBreakdown(session);
        var status = _budgetManager.CheckBudget(session, session.BudgetConfig!, breakdown.Total);
        
        if (status.Level >= BudgetLevel.Critical)
        {
            session.PendingCompaction.Should().BeTrue();
        }
    }
}

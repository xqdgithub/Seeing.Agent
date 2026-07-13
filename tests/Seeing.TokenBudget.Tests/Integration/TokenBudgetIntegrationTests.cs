using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Session.Compression;
using Seeing.Session.Compression.Strategies;
using Seeing.Session.Core;
using Seeing.Session.Storage;
using Seeing.TokenBudget;
using Seeing.TokenBudget.Api;
using Seeing.TokenBudget.Configuration;
using Seeing.TokenBudget.Extensions;
using Seeing.TokenEstimation;
using Xunit;

namespace Seeing.TokenBudget.Tests.Integration;

public class TokenBudgetIntegrationTests
{
    private readonly IServiceProvider _services;

    public TokenBudgetIntegrationTests()
    {
        var services = new ServiceCollection();

        // Add minimal configuration
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TokenBudget:DefaultConfig:MaxContextTokens"] = "100000"
            })
            .Build();

        // Add required session dependencies
        services.AddSingleton<ISessionStore, InMemorySessionStore>();
        services.AddSingleton<ICompressionStrategy, SlidingWindowTokenStrategy>();

        services.AddTokenBudgetManagement(config);
        _services = services.BuildServiceProvider();
    }

    [Fact]
    public void CanResolveAllServices()
    {
        // Verify all services can be resolved
        _services.GetRequiredService<ITokenCounter>().Should().NotBeNull();
        _services.GetRequiredService<ITokenBudgetManager>().Should().NotBeNull();
        _services.GetRequiredService<ITokenBudgetConfigResolver>().Should().NotBeNull();
        _services.GetRequiredService<ITokenBudgetApi>().Should().NotBeNull();
        _services.GetRequiredService<ICompressionTrigger>().Should().NotBeNull();
        _services.GetRequiredService<ISessionStore>().Should().NotBeNull();
        _services.GetRequiredService<ICompressionStrategy>().Should().NotBeNull();
    }

    [Fact]
    public void FullFlow_CalculateAndCheckBudget()
    {
        // Arrange
        var manager = _services.GetRequiredService<ITokenBudgetManager>();
        var counter = _services.GetRequiredService<ITokenCounter>();

        var session = SessionData.Create();
        for (int i = 0; i < 10; i++)
        {
            session.Messages.Add(SessionMessage.UserMessage($"User message {i} with some content here"));
            session.Messages.Add(SessionMessage.AssistantMessage($"Assistant response {i} with more content"));
        }

        // Act
        var breakdown = manager.CalculateBreakdown(session);
        var config = new TokenBudgetConfig
        {
            MaxContextTokens = 100000,
            WarningThreshold = new ThresholdConfig { Percentage = 80 },
            CompactionThreshold = new ThresholdConfig { Percentage = 90 }
        };
        var status = manager.CheckBudget(session, config, breakdown.Total);

        // Assert
        breakdown.Total.Should().BeGreaterThan(0);
        breakdown.BySource.UserMessages.Should().BeGreaterThan(0);
        breakdown.BySource.AssistantMessages.Should().BeGreaterThan(0);
        status.Level.Should().Be(BudgetLevel.Normal);
        status.CurrentTokens.Should().Be(breakdown.Total);
    }

    [Fact]
    public void ConfigResolver_ResolvesMultiLevelPriority()
    {
        var resolver = _services.GetRequiredService<ITokenBudgetConfigResolver>();

        var global = new TokenBudgetConfig { MaxContextTokens = 100000 };
        var agent = new TokenBudgetConfig { MaxContextTokens = 80000 };
        var session = new TokenBudgetConfig { MaxContextTokens = 60000 };

        var result = resolver.Resolve(session, agent, global);

        result.MaxContextTokens.Should().Be(60000); // Session wins
    }

    [Fact]
    public void CompressionTrigger_DualThresholdLogic()
    {
        var trigger = _services.GetRequiredService<ICompressionTrigger>();

        // Normal level - no compression
        var normalStatus = new BudgetStatus
        {
            Level = BudgetLevel.Normal,
            CurrentTokens = 50000,
            MaxTokens = 100000
        };
        var normalDecision = trigger.ShouldTrigger(normalStatus);
        normalDecision.NeedsCompression.Should().BeFalse();

        // Critical level - deferred compression
        var criticalStatus = new BudgetStatus
        {
            Level = BudgetLevel.Critical,
            CurrentTokens = 92000,
            MaxTokens = 100000
        };
        var criticalDecision = trigger.ShouldTrigger(criticalStatus);
        criticalDecision.NeedsCompression.Should().BeTrue();
        criticalDecision.Immediate.Should().BeFalse();

        // Overflow level - immediate compression
        var overflowStatus = new BudgetStatus
        {
            Level = BudgetLevel.Overflow,
            CurrentTokens = 110000,
            MaxTokens = 100000
        };
        var overflowDecision = trigger.ShouldTrigger(overflowStatus);
        overflowDecision.NeedsCompression.Should().BeTrue();
        overflowDecision.Immediate.Should().BeTrue();
    }
}

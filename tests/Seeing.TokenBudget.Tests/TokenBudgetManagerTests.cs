using FluentAssertions;
using Seeing.Session.Core;
using Seeing.TokenBudget;
using Seeing.TokenEstimation;
using Xunit;

namespace Seeing.TokenBudget.Tests;

public class TokenBudgetManagerTests
{
    private readonly TokenBudgetManager _manager;

    public TokenBudgetManagerTests()
    {
        _manager = new TokenBudgetManager();
    }

    #region DetermineLevel Tests

    [Theory]
    [InlineData(50, BudgetLevel.Normal)]      // 50% < 75% warning (default warning is 80%, but we're testing against compaction which is 90%)
    [InlineData(80, BudgetLevel.Warning)]     // 80% >= 80% warning
    [InlineData(92, BudgetLevel.Critical)]    // 92% >= 90% critical (compaction threshold)
    [InlineData(105, BudgetLevel.Overflow)]   // > 100% overflow
    public void DetermineLevel_VariousUsageLevels_ReturnsCorrectLevel(int usagePercent, BudgetLevel expected)
    {
        // Arrange
        var config = new TokenBudgetConfig
        {
            WarningThreshold = new ThresholdConfig { Percentage = 80 },
            CompactionThreshold = new ThresholdConfig { Percentage = 90 }
        };
        const int maxTokens = 100000;
        var currentTokens = (int)(maxTokens * usagePercent / 100.0);

        // Act
        var result = _manager.DetermineLevel(currentTokens, maxTokens, config);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void DetermineLevel_AtExactWarningThreshold_ReturnsWarning()
    {
        // Arrange
        var config = new TokenBudgetConfig
        {
            WarningThreshold = new ThresholdConfig { Percentage = 80 },
            CompactionThreshold = new ThresholdConfig { Percentage = 90 }
        };
        const int maxTokens = 100000;
        const int currentTokens = 80000; // Exactly 80%

        // Act
        var result = _manager.DetermineLevel(currentTokens, maxTokens, config);

        // Assert
        result.Should().Be(BudgetLevel.Warning);
    }

    [Fact]
    public void DetermineLevel_AtExactCompactionThreshold_ReturnsCritical()
    {
        // Arrange
        var config = new TokenBudgetConfig
        {
            WarningThreshold = new ThresholdConfig { Percentage = 80 },
            CompactionThreshold = new ThresholdConfig { Percentage = 90 }
        };
        const int maxTokens = 100000;
        const int currentTokens = 90000; // Exactly 90%

        // Act
        var result = _manager.DetermineLevel(currentTokens, maxTokens, config);

        // Assert
        result.Should().Be(BudgetLevel.Critical);
    }

    [Fact]
    public void DetermineLevel_ZeroMaxTokens_ReturnsNormal()
    {
        // Arrange
        var config = new TokenBudgetConfig();
        const int maxTokens = 0;
        const int currentTokens = 0;

        // Act
        var result = _manager.DetermineLevel(currentTokens, maxTokens, config);

        // Assert
        result.Should().Be(BudgetLevel.Normal);
    }

    [Fact]
    public void DetermineLevel_WithAbsoluteThresholds_ReturnsCorrectLevel()
    {
        // Arrange
        var config = new TokenBudgetConfig
        {
            WarningThreshold = new ThresholdConfig { AbsoluteTokens = 80000 },
            CompactionThreshold = new ThresholdConfig { AbsoluteTokens = 90000 }
        };
        const int maxTokens = 100000;

        // Act & Assert
        _manager.DetermineLevel(50000, maxTokens, config).Should().Be(BudgetLevel.Normal);
        _manager.DetermineLevel(80000, maxTokens, config).Should().Be(BudgetLevel.Warning);
        _manager.DetermineLevel(90000, maxTokens, config).Should().Be(BudgetLevel.Critical);
        _manager.DetermineLevel(100001, maxTokens, config).Should().Be(BudgetLevel.Overflow);
    }

    #endregion

    #region CheckBudget Tests

    [Fact]
    public void CheckBudget_ReturnsCorrectStatus()
    {
        // Arrange
        var session = SessionData.Create();
        var config = new TokenBudgetConfig
        {
            MaxContextTokens = 100000,
            WarningThreshold = new ThresholdConfig { Percentage = 80 },
            CompactionThreshold = new ThresholdConfig { Percentage = 90 }
        };
        const int currentTokens = 85000;

        // Act
        var result = _manager.CheckBudget(session, config, currentTokens);

        // Assert
        result.CurrentTokens.Should().Be(currentTokens);
        result.MaxTokens.Should().Be(100000);
        result.Level.Should().Be(BudgetLevel.Warning); // 85% is >= 80% warning but < 90% critical
        result.UsagePercentage.Should().BeApproximately(85.0, 0.1);
        result.AvailableTokens.Should().Be(15000);
    }

    [Fact]
    public void CheckBudget_WithNullMaxContextTokens_UsesDefault()
    {
        // Arrange
        var session = SessionData.Create();
        var config = new TokenBudgetConfig
        {
            MaxContextTokens = null // Should use a default
        };
        const int currentTokens = 50000;

        // Act
        var result = _manager.CheckBudget(session, config, currentTokens);

        // Assert
        result.CurrentTokens.Should().Be(currentTokens);
        result.MaxTokens.Should().BeGreaterThan(0);
    }

    [Fact]
    public void CheckBudget_WithBreakdown_StoresBreakdown()
    {
        // Arrange
        var session = SessionData.Create();
        session.Messages.Add(SessionMessage.UserMessage("Hello world"));
        
        var config = new TokenBudgetConfig
        {
            MaxContextTokens = 100000
        };
        const int currentTokens = 5000;

        // Act
        var result = _manager.CheckBudget(session, config, currentTokens);

        // Assert
        result.Breakdown.Should().NotBeNull();
    }

    #endregion

    #region CalculateBreakdown Tests

    [Fact]
    public void CalculateBreakdown_WithMessages_ReturnsCorrectDistribution()
    {
        // Arrange
        var session = SessionData.Create();
        session.Messages.Add(SessionMessage.SystemMessage("System prompt here"));
        session.Messages.Add(SessionMessage.UserMessage("User message content"));
        session.Messages.Add(SessionMessage.AssistantMessage("Assistant response"));
        session.Messages.Add(SessionMessage.ToolMessage("Tool result", "call-123", "test-tool"));

        // Act
        var result = _manager.CalculateBreakdown(session);

        // Assert
        // System message in messages list gets added to SystemPrompt
        result.BySource.SystemPrompt.Should().BeGreaterThan(0); // System message in messages
        result.BySource.ToolDefinitions.Should().Be(0); // No toolTokens param
        
        // Each message has Content counted - 4 chars per token
        // System message: "System prompt here" = 19 chars / 4 = ~5 tokens
        result.ByRole.System.Should().Be(result.BySource.SystemPrompt);
        
        // User message: "User message content" = 21 chars / 4 = ~5 tokens
        result.ByRole.User.Should().BeGreaterThan(0);
        result.BySource.UserMessages.Should().Be(result.ByRole.User);
        
        // Assistant message: "Assistant response" = 19 chars / 4 = ~5 tokens
        result.ByRole.Assistant.Should().BeGreaterThan(0);
        result.BySource.AssistantMessages.Should().Be(result.ByRole.Assistant);
        
        // Tool message: "Tool result" = 11 chars / 4 = ~3 tokens
        result.ByRole.Tool.Should().BeGreaterThan(0);
        result.BySource.ToolResults.Should().Be(result.ByRole.Tool);
        
        // Total should match
        result.Total.Should().Be(result.BySource.Total);
    }

    [Fact]
    public void CalculateBreakdown_WithSystemPrompt_CountsSystemPrompt()
    {
        // Arrange
        var session = SessionData.Create();
        const string systemPrompt = "You are a helpful AI assistant.";

        // Act
        var result = _manager.CalculateBreakdown(session, systemPrompt);

        // Assert
        result.BySource.SystemPrompt.Should().BeGreaterThan(0);
        result.ByRole.System.Should().Be(result.BySource.SystemPrompt);
    }

    [Fact]
    public void CalculateBreakdown_WithToolTokens_CountsToolDefinitions()
    {
        // Arrange
        var session = SessionData.Create();
        const int toolTokens = 5000;

        // Act
        var result = _manager.CalculateBreakdown(session, toolTokens: toolTokens);

        // Assert
        result.BySource.ToolDefinitions.Should().Be(toolTokens);
    }

    [Fact]
    public void CalculateBreakdown_WithReasoningContent_CountsReasoning()
    {
        // Arrange
        var session = SessionData.Create();
        session.Messages.Add(SessionMessage.AssistantMessageWithReasoning(
            "The answer is 42.",
            "Let me think about this step by step..."
        ));

        // Act
        var result = _manager.CalculateBreakdown(session);

        // Assert
        // Should count both Content and ReasoningContent
        result.ByRole.Assistant.Should().BeGreaterThan(0);
        result.BySource.AssistantMessages.Should().Be(result.ByRole.Assistant);
    }

    [Fact]
    public void CalculateBreakdown_EmptySession_ReturnsZero()
    {
        // Arrange
        var session = SessionData.Create();

        // Act
        var result = _manager.CalculateBreakdown(session);

        // Assert
        result.Total.Should().Be(0);
        result.BySource.Total.Should().Be(0);
        result.ByRole.Total.Should().Be(0);
    }

    [Fact]
    public void CalculateBreakdown_WithAllSources_AggregatesCorrectly()
    {
        // Arrange
        var session = SessionData.Create();
        session.Messages.Add(SessionMessage.UserMessage("User question"));
        session.Messages.Add(SessionMessage.AssistantMessage("Assistant answer"));
        session.Messages.Add(SessionMessage.ToolMessage("Tool result", "call-1"));

        const string systemPrompt = "System instructions";
        const int toolTokens = 1000;

        // Act
        var result = _manager.CalculateBreakdown(session, systemPrompt, toolTokens);

        // Assert
        result.BySource.SystemPrompt.Should().BeGreaterThan(0);
        result.BySource.ToolDefinitions.Should().Be(toolTokens);
        result.BySource.UserMessages.Should().BeGreaterThan(0);
        result.BySource.AssistantMessages.Should().BeGreaterThan(0);
        result.BySource.ToolResults.Should().BeGreaterThan(0);
        
        // Total should be sum of all sources
        result.Total.Should().Be(
            result.BySource.SystemPrompt +
            result.BySource.ToolDefinitions +
            result.BySource.UserMessages +
            result.BySource.AssistantMessages +
            result.BySource.ToolResults
        );
    }

    #endregion

    #region Custom TokenCounter Tests

    [Fact]
    public void Constructor_WithCustomTokenCounter_UsesCustomCounter()
    {
        // Arrange
        var customCounter = new MockTokenCounter(10); // Always returns 10 tokens
        var manager = new TokenBudgetManager(customCounter);
        var session = SessionData.Create();
        session.Messages.Add(SessionMessage.UserMessage("Test message"));

        // Act
        var result = manager.CalculateBreakdown(session);

        // Assert
        result.ByRole.User.Should().Be(10);
    }

    #endregion

    #region Helper Classes

    private class MockTokenCounter : ITokenCounter
    {
        private readonly int _fixedTokenCount;

        public MockTokenCounter(int fixedTokenCount)
        {
            _fixedTokenCount = fixedTokenCount;
        }

        public int Estimate(string content) => _fixedTokenCount;
    }

    #endregion
}

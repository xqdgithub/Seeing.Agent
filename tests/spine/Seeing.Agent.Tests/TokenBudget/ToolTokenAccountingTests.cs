using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Hosting.Execution;
using Seeing.Agent.TokenBudget;
using Seeing.Agent.TokenBudget.Configuration;
using Seeing.Session.Core;
using Seeing.TokenEstimation;
using Xunit;

namespace Seeing.Agent.Tests.TokenBudget;

/// <summary>
/// 工具定义 token 计量单点归属测试：估算在 Hosting（EstimateSchemaTokens），
/// 归属在 SessionData 缓存字段，消费在 TokenBudget 分解（读 session，非跨包通道）。
/// </summary>
public class ToolTokenAccountingTests
{
    private static TokenBudgetManager NewManager(ITokenCounter counter) => new(
        Mock.Of<IModelConfigLookup>(),
        Mock.Of<IOptionsMonitor<TokenBudgetOptions>>(m => m.CurrentValue == new TokenBudgetOptions()),
        counter);

    private static IReadOnlyList<FunctionToolSchema> ThreeSchemas() => new[]
    {
        new FunctionToolSchema { Function = new FunctionSchema { Name = "read", Description = "Read a file from disk" } },
        new FunctionToolSchema { Function = new FunctionSchema { Name = "write", Description = "Write a file to disk" } },
        new FunctionToolSchema { Function = new FunctionSchema { Name = "grep", Description = "Search contents by pattern" } },
    };

    [Fact]
    public void EstimateSchemaTokens_WithThreeSchemas_ShouldReturnPositive()
    {
        // Arrange
        var counter = new CharBasedTokenCounter();

        // Act
        var tokens = ExecutionRunner.EstimateSchemaTokens(ThreeSchemas(), counter);

        // Assert
        tokens.Should().BeGreaterThan(0);
    }

    [Fact]
    public void EstimateSchemaTokens_ShouldBeDeterministic()
    {
        // Arrange
        var counter = new CharBasedTokenCounter();

        // Act
        var first = ExecutionRunner.EstimateSchemaTokens(ThreeSchemas(), counter);
        var second = ExecutionRunner.EstimateSchemaTokens(ThreeSchemas(), counter);

        // Assert
        first.Should().Be(second);
    }

    [Fact]
    public void CalculateBreakdown_ReadsSessionCachedTokens_ShouldPopulateToolDefinitionsAndCatalog()
    {
        // Arrange
        var manager = NewManager(new CharBasedTokenCounter());
        var session = SessionData.Create();
        session.CachedSchemaTokens = 123;
        session.CachedCatalogTokens = 45;

        // Act
        var breakdown = manager.CalculateBreakdown(session);

        // Assert：读自会话缓存，工具定义与目录分列
        breakdown.BySource.ToolDefinitions.Should().Be(123);
        breakdown.BySource.Catalog.Should().Be(45);
        breakdown.Total.Should().Be(168);
    }

    [Fact]
    public void CalculateBreakdown_WithoutCachedTokens_ShouldBeZero()
    {
        // Arrange
        var manager = NewManager(new CharBasedTokenCounter());
        var session = SessionData.Create();

        // Act
        var breakdown = manager.CalculateBreakdown(session);

        // Assert：未结算（缓存为 null）时口径为 0，不再依赖无人填的 toolTokens 形参
        breakdown.BySource.ToolDefinitions.Should().Be(0);
        breakdown.BySource.Catalog.Should().Be(0);
    }
}

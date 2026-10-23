using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.TokenBudget;
using Seeing.Agent.TokenBudget.Configuration;
using Seeing.Session.Core;
using Seeing.TokenEstimation;
using Xunit;

namespace Seeing.Agent.Tests.TokenBudget;

/// <summary>
/// 工具定义 token 归属消费侧测试：TokenBudget 分解读 SessionData 缓存（单点归属，非跨包通道）。
/// 估算产生侧契约（resolver 单点）见 SessionToolSchemaResolverTests（组12）。
/// </summary>
public class ToolTokenAccountingTests
{
    private static TokenBudgetManager NewManager(ITokenCounter counter) => new(
        Mock.Of<IModelConfigLookup>(),
        Mock.Of<IOptionsMonitor<TokenBudgetOptions>>(m => m.CurrentValue == new TokenBudgetOptions()),
        counter);

    [Fact]
    public void CalculateBreakdown_ReadsSessionCachedTokens_ShouldPopulateToolDefinitionsAndCatalog()
    {
        // Arrange
        var manager = NewManager(new CharBasedTokenCounter());
        var session = SessionData.Create();
        session.ToolExposure.Commit(null, false, 123, 45);


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

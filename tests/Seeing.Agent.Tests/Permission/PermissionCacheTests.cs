using FluentAssertions;
using Moq;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Permission;
using Xunit;

namespace Seeing.Agent.Tests.Permission;

public class PermissionCacheTests
{
    private readonly Mock<IRuleEngine> _mockRuleEngine;
    private readonly PermissionCache _cache;

    public PermissionCacheTests()
    {
        _mockRuleEngine = new Mock<IRuleEngine>();
        _cache = new PermissionCache(_mockRuleEngine.Object);
    }

    [Fact]
    public void Get_WhenCacheMiss_EvaluatesViaRuleEngine()
    {
        // Arrange
        var key = new PermissionCacheKey("read", "*");
        _mockRuleEngine.Setup(x => x.Evaluate("read", "*")).Returns(PermissionAction.Allow);

        // Act
        var result = _cache.Get(key);

        // Assert
        result.Should().Be(PermissionAction.Allow);
        _mockRuleEngine.Verify(x => x.Evaluate("read", "*"), Times.Once);
    }

    [Fact]
    public void Get_WhenCacheHit_ReturnsCachedValue()
    {
        // Arrange
        var key = new PermissionCacheKey("write", "*");
        _mockRuleEngine.Setup(x => x.Evaluate("write", "*")).Returns(PermissionAction.Deny);
        
        // First call - cache miss
        _cache.Get(key);
        
        // Act - second call should use cache
        var result = _cache.Get(key);

        // Assert - RuleEngine should only be called once
        result.Should().Be(PermissionAction.Deny);
        _mockRuleEngine.Verify(x => x.Evaluate("write", "*"), Times.Once);
    }

    [Fact]
    public void Invalidate_RemovesCachedEntry()
    {
        // Arrange
        var key = new PermissionCacheKey("bash", "*");
        _mockRuleEngine.Setup(x => x.Evaluate("bash", "*")).Returns(PermissionAction.Ask);
        _cache.Get(key);

        // Act
        _cache.Invalidate(key);
        _cache.Get(key);

        // Assert - should evaluate twice after invalidation
        _mockRuleEngine.Verify(x => x.Evaluate("bash", "*"), Times.Exactly(2));
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        // Arrange
        _mockRuleEngine.Setup(x => x.Evaluate(It.IsAny<string>(), It.IsAny<string>())).Returns(PermissionAction.Allow);
        _cache.Get(new PermissionCacheKey("read", "*"));
        _cache.Get(new PermissionCacheKey("write", "*"));

        // Act
        _cache.Clear();
        var (total, _) = _cache.GetStats();

        // Assert
        total.Should().Be(0);
    }

    [Fact]
    public void CacheKey_Equals_HandlesSameValues()
    {
        // Arrange
        var key1 = new PermissionCacheKey("read", "*", "agent1");
        var key2 = new PermissionCacheKey("read", "*", "agent1");

        // Act & Assert
        key1.Should().Be(key2);
        key1.GetHashCode().Should().Be(key2.GetHashCode());
    }

    [Fact]
    public void CacheKey_NotEquals_DifferentValues()
    {
        // Arrange
        var key1 = new PermissionCacheKey("read", "*", "agent1");
        var key2 = new PermissionCacheKey("read", "*", "agent2");

        // Act & Assert
        key1.Should().NotBe(key2);
    }
}
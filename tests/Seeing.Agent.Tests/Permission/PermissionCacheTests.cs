using FluentAssertions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Permission;
using Xunit;

namespace Seeing.Agent.Tests.Permission;

public class PermissionCacheTests
{
    private readonly PermissionCache _cache;

    public PermissionCacheTests()
    {
        _cache = new PermissionCache();
    }

    [Fact]
    public void Get_WhenCacheMiss_ReturnsDeny()
    {
        // Arrange
        var key = new PermissionCacheKey("read", "*");

        // Act
        var result = _cache.Get(key);

        // Assert - cache miss returns Deny (no automatic evaluation)
        result.Should().Be(PermissionAction.Deny);
    }

    [Fact]
    public void Get_WhenCacheHit_ReturnsCachedValue()
    {
        // Arrange
        var key = new PermissionCacheKey("write", "*");
        _cache.Set(key, PermissionAction.Deny);

        // Act - second call should use cache
        var result = _cache.Get(key);

        // Assert
        result.Should().Be(PermissionAction.Deny);
    }

    [Fact]
    public void TryGet_WhenCacheMiss_ReturnsFalse()
    {
        // Arrange
        var key = new PermissionCacheKey("bash", "*");

        // Act
        var found = _cache.TryGet(key, out var action);

        // Assert
        found.Should().BeFalse();
        action.Should().Be(PermissionAction.Deny);
    }

    [Fact]
    public void TryGet_WhenCacheHit_ReturnsTrueAndCachedValue()
    {
        // Arrange
        var key = new PermissionCacheKey("bash", "*");
        _cache.Set(key, PermissionAction.Ask);

        // Act
        var found = _cache.TryGet(key, out var action);

        // Assert
        found.Should().BeTrue();
        action.Should().Be(PermissionAction.Ask);
    }

    [Fact]
    public void Invalidate_RemovesCachedEntry()
    {
        // Arrange
        var key = new PermissionCacheKey("bash", "*");
        _cache.Set(key, PermissionAction.Ask);

        // Act
        _cache.Invalidate(key);
        var found = _cache.TryGet(key, out _);

        // Assert
        found.Should().BeFalse();
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        // Arrange
        _cache.Set(new PermissionCacheKey("read", "*"), PermissionAction.Allow);
        _cache.Set(new PermissionCacheKey("write", "*"), PermissionAction.Deny);

        // Act
        _cache.Clear();
        var (total, _) = _cache.GetStats();

        // Assert
        total.Should().Be(0);
    }

    [Fact]
    public void Set_WithCustomTtl_StoresEntry()
    {
        // Arrange
        var key = new PermissionCacheKey("read", "*");
        _cache.Set(key, PermissionAction.Allow, TimeSpan.FromMinutes(10));

        // Act
        var found = _cache.TryGet(key, out var action);

        // Assert
        found.Should().BeTrue();
        action.Should().Be(PermissionAction.Allow);
    }

    [Fact]
    public void InvalidateByPermission_RemovesMatchingEntries()
    {
        // Arrange
        var key1 = new PermissionCacheKey("file_read", "/public/*");
        var key2 = new PermissionCacheKey("file_write", "/private/*");
        _cache.Set(key1, PermissionAction.Allow);
        _cache.Set(key2, PermissionAction.Deny);

        // Act
        _cache.InvalidateByPermission("file_read");

        // Assert
        _cache.TryGet(key1, out _).Should().BeFalse();
        _cache.TryGet(key2, out var action).Should().BeTrue();
        action.Should().Be(PermissionAction.Deny);
    }

    [Fact]
    public void InvalidateByAgent_RemovesMatchingEntries()
    {
        // Arrange
        var key1 = new PermissionCacheKey("read", "*", "agent1");
        var key2 = new PermissionCacheKey("read", "*", "agent2");
        _cache.Set(key1, PermissionAction.Allow);
        _cache.Set(key2, PermissionAction.Deny);

        // Act
        _cache.InvalidateByAgent("agent1");

        // Assert
        _cache.TryGet(key1, out _).Should().BeFalse();
        _cache.TryGet(key2, out var action).Should().BeTrue();
        action.Should().Be(PermissionAction.Deny);
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

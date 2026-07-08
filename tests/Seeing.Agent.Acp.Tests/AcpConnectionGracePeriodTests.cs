using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Acp.Session;
using Seeing.Agent.Configuration;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Acp.Tests;

/// <summary>
/// ACP Session 宽限期功能测试
/// </summary>
public class AcpConnectionGracePeriodTests
{
    [Fact]
    public void AcpOptions_DefaultGracePeriod_IsFiveMinutes()
    {
        // Arrange & Act
        var options = new AcpOptions();

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(5), options.SessionGracePeriod);
    }

    [Fact]
    public void AcpOptions_GracePeriod_CanBeSetToZero()
    {
        // Arrange & Act
        var options = new AcpOptions
        {
            SessionGracePeriod = TimeSpan.Zero
        };

        // Assert
        Assert.Equal(TimeSpan.Zero, options.SessionGracePeriod);
    }

    [Fact]
    public void AcpOptions_GracePeriod_CanBeSetToCustomValue()
    {
        // Arrange & Act
        var options = new AcpOptions
        {
            SessionGracePeriod = TimeSpan.FromMinutes(10)
        };

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(10), options.SessionGracePeriod);
    }

    [Fact]
    public void AcpSessionStore_CacheMapping_StoresInCache()
    {
        // Arrange
        var mockSessionManager = new Mock<ISessionManager>();
        var store = new AcpSessionStore(mockSessionManager.Object, NullLogger<AcpSessionStore>.Instance);
        var mapping = new AcpSessionMapping { BackendId = "test", AcpSessionId = "acp-123" };

        // Act
        store.CacheMapping("session-1", mapping);

        // Assert
        Assert.True(store.HasCachedMapping("session-1"));
        var retrieved = store.GetMapping("session-1");
        Assert.NotNull(retrieved);
        Assert.Equal("test", retrieved.BackendId);
        Assert.Equal("acp-123", retrieved.AcpSessionId);
    }

    [Fact]
    public void AcpSessionStore_ClearCachedMapping_RemovesFromCache()
    {
        // Arrange
        var mockSessionManager = new Mock<ISessionManager>();
        var store = new AcpSessionStore(mockSessionManager.Object, NullLogger<AcpSessionStore>.Instance);
        var mapping = new AcpSessionMapping { BackendId = "test", AcpSessionId = "acp-123" };
        store.CacheMapping("session-1", mapping);

        // Act
        store.ClearCachedMapping("session-1");

        // Assert
        Assert.False(store.HasCachedMapping("session-1"));
    }

    [Fact]
    public void AcpSessionStore_GetMapping_PrefersCacheOverSession()
    {
        // Arrange
        var mockSessionManager = new Mock<ISessionManager>();
        mockSessionManager.Setup(x => x.Get("session-1")).Returns(new SessionData { Id = "session-1", Metadata = new Dictionary<string, string>() });
        
        var store = new AcpSessionStore(mockSessionManager.Object, NullLogger<AcpSessionStore>.Instance);
        var cachedMapping = new AcpSessionMapping { BackendId = "cached", AcpSessionId = "cached-acp" };
        store.CacheMapping("session-1", cachedMapping);

        // Act
        var result = store.GetMapping("session-1");

        // Assert - 应该返回缓存的 mapping，而不是尝试从 session 获取
        Assert.NotNull(result);
        Assert.Equal("cached", result.BackendId);
        Assert.Equal("cached-acp", result.AcpSessionId);
    }

    [Fact]
    public void AcpSessionStore_CacheMapping_CanBeCalledMultipleTimes()
    {
        // Arrange
        var mockSessionManager = new Mock<ISessionManager>();
        var store = new AcpSessionStore(mockSessionManager.Object, NullLogger<AcpSessionStore>.Instance);
        var mapping1 = new AcpSessionMapping { BackendId = "test1", AcpSessionId = "acp-1" };
        var mapping2 = new AcpSessionMapping { BackendId = "test2", AcpSessionId = "acp-2" };

        // Act
        store.CacheMapping("session-1", mapping1);
        store.CacheMapping("session-1", mapping2);

        // Assert - 后一次应该覆盖前一次
        var retrieved = store.GetMapping("session-1");
        Assert.NotNull(retrieved);
        Assert.Equal("test2", retrieved.BackendId);
        Assert.Equal("acp-2", retrieved.AcpSessionId);
    }

    [Fact]
    public void AcpSessionStore_HasCachedMapping_ReturnsFalseForNonExistent()
    {
        // Arrange
        var mockSessionManager = new Mock<ISessionManager>();
        var store = new AcpSessionStore(mockSessionManager.Object, NullLogger<AcpSessionStore>.Instance);

        // Act & Assert
        Assert.False(store.HasCachedMapping("non-existent"));
    }

    [Fact]
    public void AcpSessionMapping_SerializeAndParse_WorkCorrectly()
    {
        // Arrange
        var mapping = new AcpSessionMapping { BackendId = "opencode", AcpSessionId = "session-abc123" };

        // Act
        var serialized = mapping.Serialize();
        var parsed = AcpSessionMapping.TryParse(serialized);

        // Assert
        Assert.NotNull(parsed);
        Assert.Equal("opencode", parsed.BackendId);
        Assert.Equal("session-abc123", parsed.AcpSessionId);
    }

    [Fact]
    public void SeeingAgentOptions_Acp_HasGracePeriod()
    {
        // Arrange & Act
        var options = new SeeingAgentOptions();

        // Assert
        Assert.NotNull(options.Acp);
        Assert.Equal(TimeSpan.FromMinutes(5), options.Acp.SessionGracePeriod);
    }
}
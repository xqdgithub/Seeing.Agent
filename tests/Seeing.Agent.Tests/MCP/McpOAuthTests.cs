using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.MCP.OAuth;
using Xunit;

namespace Seeing.Agent.Tests.MCP;

public class McpOAuthProviderTests
{
    [Fact]
    public void McpOAuthToken_ShouldDetectExpired()
    {
        // Arrange
        var token = new McpOAuthToken
        {
            AccessToken = "test",
            ExpiresIn = -1 // Already expired
        };

        // Act & Assert
        token.IsExpired.Should().BeTrue();
    }

    [Fact]
    public void McpOAuthToken_ShouldDetectExpiringSoon()
    {
        // Arrange
        var token = new McpOAuthToken
        {
            AccessToken = "test",
            ExpiresIn = 60 // 1 minute left
        };

        // Act & Assert
        token.IsExpiringSoon.Should().BeTrue();
        token.IsExpired.Should().BeFalse();
    }

    [Fact]
    public void McpOAuthToken_ShouldCalculateExpiresAt()
    {
        // Arrange
        var createdAt = DateTimeOffset.UtcNow;
        var token = new McpOAuthToken
        {
            AccessToken = "test",
            ExpiresIn = 3600,
            CreatedAt = createdAt
        };

        // Act
        var expiresAt = token.ExpiresAt;

        // Assert
        expiresAt.Should().Be(createdAt.AddSeconds(3600));
    }

    [Fact]
    public void McpOAuthConfig_Defaults()
    {
        // Arrange & Act
        var config = new McpOAuthConfig();

        // Assert
        config.UsePkce.Should().BeTrue();
        config.Disabled.Should().BeFalse();
    }

    [Fact]
    public void McpOAuthException_ShouldCaptureDetails()
    {
        // Arrange & Act
        var ex = new McpOAuthException("Test error", "test-server", McpAuthStatus.NeedsAuthorization);

        // Assert
        ex.Message.Should().Contain("Test error");
        ex.McpName.Should().Be("test-server");
        ex.Status.Should().Be(McpAuthStatus.NeedsAuthorization);
    }
}

public class McpOAuthStorageTests
{
    private static McpOAuthStorage CreateStorage()
    {
        var logger = new Mock<ILogger<McpOAuthStorage>>();
        return new McpOAuthStorage(logger.Object);
    }

    [Fact]
    public async Task McpOAuthStorage_SaveAndLoad_ShouldRoundTrip()
    {
        // Arrange
        var storage = CreateStorage();
        var serverName = $"test-server-{Guid.NewGuid():N}";
        var token = new McpOAuthToken
        {
            AccessToken = "access-123",
            RefreshToken = "refresh-456",
            TokenType = "Bearer",
            ExpiresIn = 3600,
            Scope = "read write"
        };

        // Act
        await storage.SaveTokenAsync(serverName, token);
        var loaded = await storage.LoadTokenAsync(serverName);

        // Assert
        loaded.Should().NotBeNull();
        loaded!.AccessToken.Should().Be("access-123");
        loaded.RefreshToken.Should().Be("refresh-456");
        loaded.TokenType.Should().Be("Bearer");
        loaded.Scope.Should().Be("read write");

        // Cleanup
        await storage.DeleteTokenAsync(serverName);
    }

    [Fact]
    public async Task McpOAuthStorage_LoadNonExistent_ShouldReturnNull()
    {
        // Arrange
        var storage = CreateStorage();
        var serverName = $"non-existent-{Guid.NewGuid():N}";

        // Act
        var token = await storage.LoadTokenAsync(serverName);

        // Assert
        token.Should().BeNull();
    }
}

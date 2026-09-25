using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Mcp.Configuration;
using Xunit;

namespace Seeing.Agent.Mcp.Tests.Configuration;

/// <summary>
/// mcp.json 中 oauth 节的解析与序列化：OAuth 配置必须能经配置文件提供，
/// 与程序化构造 McpServerConfig.OAuth 等价（McpClientManager.GetConfig 读取路径因此生效）。
/// </summary>
public class McpConfigPersistenceOAuthTests
{
    private static McpConfigPersistence CreateSut()
    {
        var logger = new Mock<ILogger<McpConfigPersistence>>().Object;
        var directories = new Mock<ISeeingDirectories>().Object;
        return new McpConfigPersistence(logger, directories);
    }

    private static JsonElement ParseJson(string json)
        => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ParseServerConfig_WithOAuthSection_ShouldParseAllFields()
    {
        const string json = """
        {
          "type": "streamableHttp",
          "url": "https://mcp.example.com/mcp",
          "oauth": {
            "authorizationEndpoint": "https://auth.example.com/authorize",
            "tokenEndpoint": "https://auth.example.com/token",
            "clientId": "client-1",
            "clientSecret": "secret-1",
            "scope": "read write",
            "redirectUri": "http://localhost:59124/callback",
            "disabled": true,
            "usePkce": false
          }
        }
        """;

        var config = CreateSut().ParseServerConfig("s1", ParseJson(json));

        config.Should().NotBeNull();
        config!.OAuth.Should().NotBeNull();
        config.OAuth!.AuthorizationEndpoint.Should().Be("https://auth.example.com/authorize");
        config.OAuth.TokenEndpoint.Should().Be("https://auth.example.com/token");
        config.OAuth.ClientId.Should().Be("client-1");
        config.OAuth.ClientSecret.Should().Be("secret-1");
        config.OAuth.Scope.Should().Be("read write");
        config.OAuth.RedirectUri.Should().Be("http://localhost:59124/callback");
        config.OAuth.Disabled.Should().BeTrue();
        config.OAuth.UsePkce.Should().BeFalse();
    }

    [Fact]
    public void ParseServerConfig_WithoutOAuthSection_ShouldLeaveOAuthNull()
    {
        const string json = """
        {
          "type": "streamableHttp",
          "url": "https://mcp.example.com/mcp"
        }
        """;

        var config = CreateSut().ParseServerConfig("s1", ParseJson(json));

        config.Should().NotBeNull();
        config!.OAuth.Should().BeNull();
    }

    [Fact]
    public void ParseServerConfig_WithPartialOAuthSection_ShouldTolerateMissingFields()
    {
        const string json = """
        {
          "type": "streamableHttp",
          "url": "https://mcp.example.com/mcp",
          "oauth": {
            "authorizationEndpoint": "https://auth.example.com/authorize"
          }
        }
        """;

        var config = CreateSut().ParseServerConfig("s1", ParseJson(json));

        config.Should().NotBeNull();
        config!.OAuth.Should().NotBeNull();
        config.OAuth!.AuthorizationEndpoint.Should().Be("https://auth.example.com/authorize");
        config.OAuth.TokenEndpoint.Should().BeNull();
        config.OAuth.ClientId.Should().BeNull();
        config.OAuth.Scope.Should().BeNull();
        config.OAuth.Disabled.Should().BeFalse();
        config.OAuth.UsePkce.Should().BeTrue();
    }

    [Fact]
    public void SerializeServerConfig_WithOAuth_ShouldWriteOAuthSection()
    {
        var config = new McpServerConfig
        {
            Name = "s1",
            TransportType = McpTransportType.StreamableHttp,
            Url = new Uri("https://mcp.example.com/mcp"),
            OAuth = new Seeing.Agent.Abstractions.Mcp.OAuth.McpOAuthConfig
            {
                AuthorizationEndpoint = "https://auth.example.com/authorize",
                TokenEndpoint = "https://auth.example.com/token",
                ClientId = "client-1",
                Scope = "read",
                UsePkce = false
            }
        };

        var json = CreateSut().SerializeServerConfig(config);
        var oauth = ParseJson(json)
            .GetProperty("mcpServers").GetProperty("s1").GetProperty("oauth");

        oauth.GetProperty("authorizationEndpoint").GetString().Should().Be("https://auth.example.com/authorize");
        oauth.GetProperty("tokenEndpoint").GetString().Should().Be("https://auth.example.com/token");
        oauth.GetProperty("clientId").GetString().Should().Be("client-1");
        oauth.GetProperty("scope").GetString().Should().Be("read");
        oauth.GetProperty("usePkce").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void SerializeServerConfig_WithoutOAuth_ShouldOmitOAuthSection()
    {
        var config = new McpServerConfig
        {
            Name = "s1",
            TransportType = McpTransportType.StreamableHttp,
            Url = new Uri("https://mcp.example.com/mcp")
        };

        var json = CreateSut().SerializeServerConfig(config);
        var server = ParseJson(json).GetProperty("mcpServers").GetProperty("s1");

        server.TryGetProperty("oauth", out _).Should().BeFalse();
    }

    [Fact]
    public void RoundTrip_OAuth_ShouldPreserveValues()
    {
        const string json = """
        {
          "type": "streamableHttp",
          "url": "https://mcp.example.com/mcp",
          "oauth": {
            "authorizationEndpoint": "https://auth.example.com/authorize",
            "tokenEndpoint": "https://auth.example.com/token",
            "clientId": "client-1",
            "clientSecret": "${SEEING_MCP_TEST_SECRET}",
            "scope": "read write",
            "redirectUri": "http://localhost:59124/callback",
            "usePkce": false
          }
        }
        """;

        var sut = CreateSut();
        var parsed = sut.ParseServerConfig("s1", ParseJson(json));

        var serializedRoot = ParseJson(sut.SerializeServerConfig(parsed!));
        var serverElement = serializedRoot
            .GetProperty("mcpServers")
            .GetProperty("s1");
        var reparsed = sut.ParseServerConfig("s1", serverElement);

        reparsed!.OAuth.Should().NotBeNull();
        reparsed.OAuth.Should().BeEquivalentTo(parsed.OAuth);
    }

    [Fact]
    public void SerializeServerConfig_WithPlaintextClientSecret_ShouldNotPersistPlaintext()
    {
        var config = new McpServerConfig
        {
            Name = "s1",
            TransportType = McpTransportType.StreamableHttp,
            Url = new Uri("https://mcp.example.com/mcp"),
            OAuth = new Seeing.Agent.Abstractions.Mcp.OAuth.McpOAuthConfig
            {
                AuthorizationEndpoint = "https://auth.example.com/authorize",
                TokenEndpoint = "https://auth.example.com/token",
                ClientId = "client-1",
                ClientSecret = "super-secret-value"
            }
        };

        var json = CreateSut().SerializeServerConfig(config);

        json.Should().NotContain("super-secret-value");
        var oauth = ParseJson(json).GetProperty("mcpServers").GetProperty("s1").GetProperty("oauth");
        oauth.TryGetProperty("clientSecret", out _).Should().BeFalse("明文密钥不得写回配置文件");
    }

    [Fact]
    public void SerializeServerConfig_WithEnvReferenceClientSecret_ShouldPersistReference()
    {
        var config = new McpServerConfig
        {
            Name = "s1",
            TransportType = McpTransportType.StreamableHttp,
            Url = new Uri("https://mcp.example.com/mcp"),
            OAuth = new Seeing.Agent.Abstractions.Mcp.OAuth.McpOAuthConfig
            {
                AuthorizationEndpoint = "https://auth.example.com/authorize",
                TokenEndpoint = "https://auth.example.com/token",
                ClientId = "client-1",
                ClientSecret = "env:SEEING_MCP_TEST_SECRET"
            }
        };

        var json = CreateSut().SerializeServerConfig(config);

        var oauth = ParseJson(json).GetProperty("mcpServers").GetProperty("s1").GetProperty("oauth");
        oauth.GetProperty("clientSecret").GetString().Should().Be("env:SEEING_MCP_TEST_SECRET");
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Mcp.OAuth;
using Xunit;

namespace Seeing.Agent.Mcp.Tests.OAuth;

public class McpOAuthStorageEncryptionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "seeing-mcp-oauth-tests", Guid.NewGuid().ToString("N"));

    public McpOAuthStorageEncryptionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败忽略 */ }
    }

    private McpOAuthStorage CreateStorage(string fileName)
        => new(NullLogger<McpOAuthStorage>.Instance,
            storageDirectory: Path.Combine(_root, "tokens"),
            keyFilePath: Path.Combine(_root, fileName));

    [Fact]
    public async Task SaveAndLoad_ShouldRoundTrip_UsingPersistedKeyFile()
    {
        var keyPath = Path.Combine(_root, "roundtrip.key");
        var storage = CreateStorage("roundtrip.key");
        var token = new McpOAuthToken
        {
            AccessToken = "access-123",
            RefreshToken = "refresh-456",
            TokenType = "Bearer",
            ExpiresIn = 3600,
            Scope = "read write"
        };

        await storage.SaveTokenAsync("server-a", token);

        // 密钥文件必须真实落盘且为 32 字节随机密钥
        File.Exists(keyPath).Should().BeTrue();
        (await File.ReadAllBytesAsync(keyPath)).Should().HaveCount(32);

        // 新实例（同一密钥文件）应能解密
        var reopened = CreateStorage("roundtrip.key");
        var loaded = await reopened.LoadTokenAsync("server-a");

        loaded.Should().NotBeNull();
        loaded!.AccessToken.Should().Be("access-123");
        loaded.RefreshToken.Should().Be("refresh-456");
        loaded.Scope.Should().Be("read write");
    }

    [Fact]
    public async Task Load_WithDifferentKey_ShouldNotDecrypt()
    {
        // 使用密钥 A 写入
        var storageA = CreateStorage("key-a.bin");
        await storageA.SaveTokenAsync("server-b", new McpOAuthToken
        {
            AccessToken = "secret-token",
            ExpiresIn = 3600
        });

        var keyA = await File.ReadAllBytesAsync(Path.Combine(_root, "key-a.bin"));

        // 使用另一个随机密钥 B 读取同一目录下的密文
        var storageB = CreateStorage("key-b.bin");
        var loaded = await storageB.LoadTokenAsync("server-b");

        var keyB = await File.ReadAllBytesAsync(Path.Combine(_root, "key-b.bin"));

        // 密钥不可从机器信息推导：两次生成互相独立
        keyA.Should().NotEqual(keyB);
        // 错误密钥无法解密，返回 null 而非明文
        loaded.Should().BeNull();
    }

    [Fact]
    public async Task Delete_ShouldRemoveTokenFile()
    {
        var storage = CreateStorage("delete.key");
        await storage.SaveTokenAsync("server-c", new McpOAuthToken { AccessToken = "t", ExpiresIn = 60 });

        (await storage.TokenExistsAsync("server-c")).Should().BeTrue();
        await storage.DeleteTokenAsync("server-c");
        (await storage.TokenExistsAsync("server-c")).Should().BeFalse();
    }
}

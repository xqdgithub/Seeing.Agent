using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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

    private string TokenPath(string mcpName) => Path.Combine(_root, "tokens", $"{mcpName}.token");

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
    public async Task Save_ShouldWriteVersionedAesGcmCiphertext()
    {
        var storage = CreateStorage("format.key");
        await storage.SaveTokenAsync("server-format", new McpOAuthToken { AccessToken = "abc", ExpiresIn = 60 });

        var bytes = await File.ReadAllBytesAsync(TokenPath("server-format"));

        // 版本头 "MOA2" + 12B nonce + 16B tag + 密文
        Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("MOA2");
        bytes.Length.Should().BeGreaterThan(4 + 12 + 16);
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
    public async Task Load_WithTamperedCiphertext_ShouldFailClosedAndReportAuthenticationFailure()
    {
        var logger = new CapturingLogger<McpOAuthStorage>();
        var storage = new McpOAuthStorage(
            logger,
            storageDirectory: Path.Combine(_root, "tokens"),
            keyFilePath: Path.Combine(_root, "tamper.key"));

        await storage.SaveTokenAsync("server-tamper", new McpOAuthToken { AccessToken = "real", ExpiresIn = 3600 });

        // 篡改密文最后一字节（认证标签/密文区域），GCM 必须立即认证失败
        var bytes = await File.ReadAllBytesAsync(TokenPath("server-tamper"));
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(TokenPath("server-tamper"), bytes);

        var loaded = await storage.LoadTokenAsync("server-tamper");

        loaded.Should().BeNull("被篡改的密文不得解出任何内容");
        logger.Messages.Should().Contain(m => m.Contains("认证失败"));
    }

    [Fact]
    public async Task Load_WithLegacyAesCbcFormat_ShouldStillDecryptForBackwardCompatibility()
    {
        var keyPath = Path.Combine(_root, "legacy.key");
        var storage = CreateStorage("legacy.key");

        // 先保存一次以生成密钥文件
        await storage.SaveTokenAsync("server-legacy", new McpOAuthToken { AccessToken = "ignored", ExpiresIn = 1 });
        var key = await File.ReadAllBytesAsync(keyPath);

        // 手工以旧版 AES-CBC（随机 IV 前置、无认证）写入令牌
        var legacyToken = new McpOAuthToken { AccessToken = "legacy-access", RefreshToken = "legacy-refresh", ExpiresIn = 3600 };
        var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(legacyToken));

        using (var aes = Aes.Create())
        {
            aes.Key = key;
            aes.GenerateIV();
            using var encryptor = aes.CreateEncryptor();
            using var ms = new MemoryStream();
            ms.Write(aes.IV, 0, aes.IV.Length);
            using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                cs.Write(plaintext, 0, plaintext.Length);
            await File.WriteAllBytesAsync(TokenPath("server-legacy"), ms.ToArray());
        }

        var loaded = await storage.LoadTokenAsync("server-legacy");

        loaded.Should().NotBeNull();
        loaded!.AccessToken.Should().Be("legacy-access");
        loaded.RefreshToken.Should().Be("legacy-refresh");
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

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}

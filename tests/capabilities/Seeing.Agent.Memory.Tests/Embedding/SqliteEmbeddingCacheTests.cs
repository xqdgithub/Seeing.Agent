using FluentAssertions;
using Microsoft.Data.Sqlite;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core;
using Seeing.Agent.Memory.Core.Embedding;
using Seeing.Agent.Memory.Core.Models;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Embedding;

/// <summary>
/// 缓存主键含模型指纹（spec §7.8.1 连带必改项之一）。
/// </summary>
public class SqliteEmbeddingCacheTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteEmbeddingCache _cache;

    public SqliteEmbeddingCacheTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _cache = new SqliteEmbeddingCache(_connection, new SqliteConnectionGate());
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    private static string Hash(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(text)))[..16];

    [Fact]
    public async Task Cache_SameTextDifferentModel_ShouldNotHit()
    {
        // Arrange：同一段文本，A 模型产出的向量
        var hash = Hash("同一条文本");
        await _cache.SetAsync(hash, "openai/text-embedding-3-small",
            new EmbeddingResult("同一条文本", new[] { 1f, 2f, 3f }, 9, "openai/text-embedding-3-small"),
            TestContext.Current.CancellationToken);

        // Act：换成 B 模型查同一条文本
        var otherModel = await _cache.GetAsync(hash, "siliconflow/BAAI/bge-m3", TestContext.Current.CancellationToken);
        var sameModel = await _cache.GetAsync(hash, "openai/text-embedding-3-small", TestContext.Current.CancellationToken);

        // Assert：不同模型不得命中；同模型正常命中且带模型归属
        otherModel.Should().BeNull("向量空间随模型而不同，跨模型命中会把旧向量当新模型结果返回");
        sameModel.Should().NotBeNull();
        sameModel!.Vector.Should().Equal(1f, 2f, 3f);
        sameModel.ModelId.Should().Be("openai/text-embedding-3-small");
    }

    [Fact]
    public async Task SetAsync_SameModelTwice_ShouldReplaceSingleRow()
    {
        var hash = Hash("文本");
        await _cache.SetAsync(hash, "openai/a", new EmbeddingResult("文本", new[] { 1f }, 1, "openai/a"),
            TestContext.Current.CancellationToken);
        await _cache.SetAsync(hash, "openai/a", new EmbeddingResult("文本", new[] { 2f, 2f }, 2, "openai/a"),
            TestContext.Current.CancellationToken);

        var stats = await _cache.GetStatsAsync(TestContext.Current.CancellationToken);
        stats.TotalEntries.Should().Be(1);

        var hit = await _cache.GetAsync(hash, "openai/a", TestContext.Current.CancellationToken);
        hit!.Vector.Should().Equal(2f, 2f);
    }

    [Fact]
    public async Task SetAsync_SameTextTwoModels_ShouldKeepTwoEntries()
    {
        var hash = Hash("文本");
        await _cache.SetAsync(hash, "openai/a", new EmbeddingResult("文本", new[] { 1f }, 1, "openai/a"),
            TestContext.Current.CancellationToken);
        await _cache.SetAsync(hash, "openai/b", new EmbeddingResult("文本", new[] { 2f }, 1, "openai/b"),
            TestContext.Current.CancellationToken);

        var stats = await _cache.GetStatsAsync(TestContext.Current.CancellationToken);
        stats.TotalEntries.Should().Be(2);
    }

    [Fact]
    public async Task GetBatchAsync_ShouldResolvePerModelFingerprint()
    {
        var hash = Hash("批量");
        await _cache.SetAsync(hash, "openai/a", new EmbeddingResult("批量", new[] { 5f }, 1, "openai/a"),
            TestContext.Current.CancellationToken);

        var results = await _cache.GetBatchAsync(new[]
        {
            new EmbeddingCacheKey(hash, "openai/a"),
            new EmbeddingCacheKey(hash, "openai/b"),
        }, TestContext.Current.CancellationToken);

        results[0].Should().NotBeNull();
        results[1].Should().BeNull();
    }

    [Fact]
    public async Task LegacyTableWithoutFingerprint_ShouldBeRebuilt_NotThrow()
    {
        // 存量库是旧主键（只有 text_hash）：缓存属派生数据，重建为空表而不是崩溃。
        using (var legacy = _connection.CreateCommand())
        {
            legacy.CommandText = @"
                CREATE TABLE embedding_cache (
                    text_hash TEXT PRIMARY KEY,
                    vector BLOB NOT NULL,
                    dimensions INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    last_accessed TEXT NOT NULL
                );
                INSERT INTO embedding_cache (text_hash, vector, dimensions, created_at, last_accessed)
                VALUES ('deadbeef', @v, 2, '2026-01-01T00:00:00+00:00', '2026-01-01T00:00:00+00:00')";
            legacy.Parameters.AddWithValue("@v", new byte[] { 0, 0, 128, 63, 0, 0, 0, 64 });
            await legacy.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var hash = Hash("迁移后写入");
        await _cache.SetAsync(hash, "openai/a", new EmbeddingResult("迁移后写入", new[] { 1f, 2f }, 1, "openai/a"),
            TestContext.Current.CancellationToken);

        var hit = await _cache.GetAsync(hash, "openai/a", TestContext.Current.CancellationToken);
        hit!.Vector.Should().Equal(1f, 2f);

        // 旧行随表重建消失（不会有孤儿指纹维度）
        var stats = await _cache.GetStatsAsync(TestContext.Current.CancellationToken);
        stats.TotalEntries.Should().Be(1);
    }

    [Fact]
    public async Task ClearExpiredAsync_ShouldRemoveOldEntries()
    {
        var hash = Hash("过期");
        await _cache.SetAsync(hash, "openai/a", new EmbeddingResult("过期", new[] { 1f }, 1, "openai/a"),
            TestContext.Current.CancellationToken);

        var deleted = await _cache.ClearExpiredAsync(TimeSpan.FromMinutes(-1), TestContext.Current.CancellationToken);

        deleted.Should().Be(1);
        (await _cache.GetAsync(hash, "openai/a", TestContext.Current.CancellationToken)).Should().BeNull();
    }
}

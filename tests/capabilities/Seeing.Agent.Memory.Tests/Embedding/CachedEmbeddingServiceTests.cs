using FluentAssertions;
using Microsoft.Data.Sqlite;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core;
using Seeing.Agent.Memory.Core.Embedding;
using Seeing.Agent.Memory.Core.Models;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Embedding;

/// <summary>
/// 缓存包装器与模型指纹的接线（spec §7.8.1）：包装器必须把内层服务的指纹带进缓存键，
/// 否则换模型后仍会命中旧向量。
/// </summary>
public class CachedEmbeddingServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteEmbeddingCache _cache;

    public CachedEmbeddingServiceTests()
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

    [Fact]
    public async Task EmbedAsync_SecondCall_ShouldHitCacheWithoutCallingInner()
    {
        var inner = new FakeEmbedding("openai/a");
        var svc = new EmbeddingService(inner, _cache);

        var first = await svc.EmbedAsync("同一条文本", TestContext.Current.CancellationToken);
        var second = await svc.EmbedAsync("同一条文本", TestContext.Current.CancellationToken);

        inner.CallCount.Should().Be(1, "第二次应走缓存");
        second.Vector.Should().Equal(first.Vector);
    }

    [Fact]
    public async Task Cache_SameTextDifferentModel_ShouldNotHit()
    {
        var modelA = new EmbeddingService(new FakeEmbedding("openai/a"), _cache);
        var vectorA = await modelA.EmbedAsync("同一条文本", TestContext.Current.CancellationToken);

        var innerB = new FakeEmbedding("siliconflow/b");
        var modelB = new EmbeddingService(innerB, _cache);
        var vectorB = await modelB.EmbedAsync("同一条文本", TestContext.Current.CancellationToken);

        innerB.CallCount.Should().Be(1, "模型指纹变了不得复用 A 的向量");
        vectorB.Vector.Should().NotEqual(vectorA.Vector);
        vectorB.ModelId.Should().Be("siliconflow/b");
    }

    [Fact]
    public async Task EmbedBatchAsync_MixedHits_ShouldOnlyRequestMisses()
    {
        var inner = new FakeEmbedding("openai/a");
        var svc = new EmbeddingService(inner, _cache);

        await svc.EmbedAsync("已缓存", TestContext.Current.CancellationToken);
        inner.CallCount.Should().Be(1);

        var results = await svc.EmbedBatchAsync(new[] { "已缓存", "新文本" }, TestContext.Current.CancellationToken);

        inner.CallCount.Should().Be(2, "批量里只有未命中的一条需要真实调用");
        results.Should().HaveCount(2);
        // 缓存条目只存向量（既有行为）：命中项的 Text 为空、ModelId 为查询时的指纹
        results[0].Text.Should().BeEmpty();
        results[0].ModelId.Should().Be("openai/a");
        results[1].Text.Should().Be("新文本");
    }

    [Fact]
    public async Task EmbedBatchAsync_SameModel_ShouldHitCacheForAll()
    {
        var inner = new FakeEmbedding("openai/a");
        var svc = new EmbeddingService(inner, _cache);
        var texts = new[] { "一", "二", "三" };

        var fresh = await svc.EmbedBatchAsync(texts, TestContext.Current.CancellationToken);
        var again = await svc.EmbedBatchAsync(texts, TestContext.Current.CancellationToken);

        inner.CallCount.Should().Be(1);
        again.Should().HaveCount(3);
        for (var i = 0; i < texts.Length; i++)
            again[i].Vector.Should().Equal(fresh[i].Vector);
    }

    /// <summary>按模型指纹产出可区分向量的内层替身。</summary>
    private sealed class FakeEmbedding : IEmbeddingService
    {
        private readonly string _fingerprint;

        public FakeEmbedding(string fingerprint) => _fingerprint = fingerprint;

        public int CallCount { get; private set; }
        public int Dimensions => 2;
        public string ProviderName => _fingerprint;
        public string ModelFingerprint => _fingerprint;

        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(Build(text));
        }

        public Task<IReadOnlyList<EmbeddingResult>> EmbedBatchAsync(
            IReadOnlyList<string> texts,
            CancellationToken ct = default)
        {
            CallCount++;
            IReadOnlyList<EmbeddingResult> results = texts.Select(Build).ToList();
            return Task.FromResult(results);
        }

        private EmbeddingResult Build(string text)
        {
            // 向量值随指纹变化：指纹不同的两个模型的向量在断言里可区分。
            var seed = _fingerprint.GetHashCode(StringComparison.Ordinal);
            return new EmbeddingResult(text, new[] { seed / 1000f, text.Length / 10f }, 1, _fingerprint);
        }
    }
}

using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Abstractions;

/// <summary>
/// Embedding 缓存接口 - SQLite 持久化缓存
/// </summary>
public interface IEmbeddingCache
{
    /// <summary>获取缓存的 Embedding</summary>
    Task<EmbeddingResult?> GetAsync(string textHash, CancellationToken ct = default);

    /// <summary>设置缓存</summary>
    Task SetAsync(string textHash, EmbeddingResult result, CancellationToken ct = default);

    /// <summary>批量获取缓存</summary>
    Task<IReadOnlyList<EmbeddingResult?>> GetBatchAsync(
        IReadOnlyList<string> textHashes, 
        CancellationToken ct = default);

    /// <summary>清除过期缓存</summary>
    Task<int> ClearExpiredAsync(TimeSpan maxAge, CancellationToken ct = default);

    /// <summary>获取缓存统计</summary>
    Task<CacheStats> GetStatsAsync(CancellationToken ct = default);
}

/// <summary>
/// 缓存统计信息
/// </summary>
public record CacheStats(
    int TotalEntries,
    long SizeBytes,
    int Hits,
    int Misses,
    double HitRate
);

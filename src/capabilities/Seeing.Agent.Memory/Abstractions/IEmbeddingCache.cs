using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Abstractions;

/// <summary>
/// Embedding 缓存接口 - SQLite 持久化缓存
/// <para>条目主键是 <c>(文本哈希, 模型指纹)</c> 二段：同一文本在不同模型下的向量属于不同
/// 向量空间，不得互相命中（spec §7.8.1）。</para>
/// </summary>
public interface IEmbeddingCache
{
    /// <summary>获取缓存的 Embedding</summary>
    Task<EmbeddingResult?> GetAsync(
        string textHash,
        string modelFingerprint,
        CancellationToken ct = default);

    /// <summary>设置缓存</summary>
    Task SetAsync(
        string textHash,
        string modelFingerprint,
        EmbeddingResult result,
        CancellationToken ct = default);

    /// <summary>批量获取缓存</summary>
    Task<IReadOnlyList<EmbeddingResult?>> GetBatchAsync(
        IReadOnlyList<EmbeddingCacheKey> keys,
        CancellationToken ct = default);

    /// <summary>清除过期缓存</summary>
    Task<int> ClearExpiredAsync(TimeSpan maxAge, CancellationToken ct = default);

    /// <summary>获取缓存统计</summary>
    Task<CacheStats> GetStatsAsync(CancellationToken ct = default);
}

/// <summary>
/// 缓存条目主键：文本哈希 + 产出该向量的模型指纹。
/// </summary>
/// <param name="TextHash">文本的 SHA256 前缀哈希。</param>
/// <param name="ModelFingerprint">模型引用或本地实现标识。</param>
public readonly record struct EmbeddingCacheKey(string TextHash, string ModelFingerprint);

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

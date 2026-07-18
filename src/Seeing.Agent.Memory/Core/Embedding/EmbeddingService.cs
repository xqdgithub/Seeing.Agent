using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Core.Embedding;

/// <summary>
/// Embedding 服务实现 - 带缓存包装器
/// </summary>
public class EmbeddingService : IEmbeddingService
{
    private readonly IEmbeddingService _inner;
    private readonly IEmbeddingCache? _cache;
    private readonly ILogger<EmbeddingService>? _logger;

    public EmbeddingService(
        IEmbeddingService inner,
        IEmbeddingCache? cache = null,
        ILogger<EmbeddingService>? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public int Dimensions => _inner.Dimensions;

    /// <inheritdoc />
    public string ProviderName => _inner.ProviderName;

    /// <inheritdoc />
    public async Task<EmbeddingResult> EmbedAsync(string text, CancellationToken ct = default)
    {
        // 尝试从缓存获取
        if (_cache != null)
        {
            var hash = ComputeHash(text);
            var cached = await _cache.GetAsync(hash, ct);
            if (cached != null)
            {
                _logger?.LogDebug("Embedding 缓存命中: {Text}", text[..Math.Min(20, text.Length)]);
                return cached;
            }
        }

        // 调用底层服务
        var result = await _inner.EmbedAsync(text, ct);

        // 写入缓存
        if (_cache != null)
        {
            var hash = ComputeHash(text);
            await _cache.SetAsync(hash, result, ct);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EmbeddingResult>> EmbedBatchAsync(
        IReadOnlyList<string> texts, 
        CancellationToken ct = default)
    {
        if (_cache == null)
        {
            return await _inner.EmbedBatchAsync(texts, ct);
        }

        var results = new EmbeddingResult[texts.Count];
        var uncachedIndices = new List<int>();
        var uncachedTexts = new List<string>();

        // 检查缓存
        for (var i = 0; i < texts.Count; i++)
        {
            var hash = ComputeHash(texts[i]);
            var cached = await _cache.GetAsync(hash, ct);
            if (cached != null)
            {
                results[i] = cached;
            }
            else
            {
                uncachedIndices.Add(i);
                uncachedTexts.Add(texts[i]);
            }
        }

        // 批量获取未缓存的
        if (uncachedTexts.Count > 0)
        {
            var newResults = await _inner.EmbedBatchAsync(uncachedTexts, ct);

            for (var i = 0; i < uncachedIndices.Count; i++)
            {
                var idx = uncachedIndices[i];
                results[idx] = newResults[i];

                // 写入缓存
                var hash = ComputeHash(texts[idx]);
                await _cache.SetAsync(hash, newResults[i], ct);
            }
        }

        return results;
    }

    private static string ComputeHash(string text)
    {
        var hashBytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hashBytes)[..16];
    }
}

using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Core.Embedding;

/// <summary>
/// 随机向量 Embedding 服务 - 用于开发和测试
/// 生成确定性随机向量（基于文本哈希），确保相同文本产生相同向量
/// </summary>
public class RandomEmbeddingService : IEmbeddingService
{
    private readonly int _dimensions;
    private readonly ILogger<RandomEmbeddingService>? _logger;

    public RandomEmbeddingService(
        int dimensions = 384,
        ILogger<RandomEmbeddingService>? logger = null)
    {
        _dimensions = dimensions;
        _logger = logger;
    }

    public int Dimensions => _dimensions;
    public string ProviderName => "Random";

    public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken ct = default)
    {
        var vector = GenerateDeterministicVector(text);
        var tokenCount = text.Length / 4; // 粗略估算
        _logger?.LogDebug("生成随机向量: {Text} -> {Dimensions}D", text[..Math.Min(20, text.Length)], _dimensions);
        return Task.FromResult(new EmbeddingResult(text, vector, tokenCount));
    }

    public Task<IReadOnlyList<EmbeddingResult>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default)
    {
        var results = texts.Select(t =>
        {
            var vector = GenerateDeterministicVector(t);
            var tokenCount = t.Length / 4;
            return new EmbeddingResult(t, vector, tokenCount);
        }).ToList();
        return Task.FromResult<IReadOnlyList<EmbeddingResult>>(results);
    }

    private float[] GenerateDeterministicVector(string text)
    {
        var hashBytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        var vector = new float[_dimensions];

        for (var i = 0; i < _dimensions; i++)
        {
            var byteIndex = i % hashBytes.Length;
            vector[i] = (hashBytes[byteIndex] / 255.0f) * 2 - 1; // 归一化到 [-1, 1]
        }

        // 归一化向量
        var magnitude = Math.Sqrt(vector.Sum(v => v * v));
        if (magnitude > 0)
        {
            for (var i = 0; i < _dimensions; i++)
            {
                vector[i] /= (float)magnitude;
            }
        }

        return vector;
    }
}

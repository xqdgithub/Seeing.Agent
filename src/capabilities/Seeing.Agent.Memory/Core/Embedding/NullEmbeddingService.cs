using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Core.Embedding;

/// <summary>
/// 显式不可用的 Embedding 实现；调用时抛错，防止静默假向量。
/// </summary>
public sealed class NullEmbeddingService : IEmbeddingService
{
    public int Dimensions => 0;
    public string ProviderName => "null";

    public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken ct = default)
        => throw new InvalidOperationException("Embedding is not configured");

    public Task<IReadOnlyList<EmbeddingResult>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default)
        => throw new InvalidOperationException("Embedding is not configured");
}

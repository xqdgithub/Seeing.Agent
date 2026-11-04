using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Abstractions;

/// <summary>
/// Embedding 服务接口 - 支持多种 Provider
/// </summary>
public interface IEmbeddingService
{
    /// <summary>获取单个文本的 Embedding</summary>
    Task<EmbeddingResult> EmbedAsync(string text, CancellationToken ct = default);

    /// <summary>批量获取 Embedding</summary>
    Task<IReadOnlyList<EmbeddingResult>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default);

    /// <summary>Embedding 维度</summary>
    int Dimensions { get; }

    /// <summary>Provider 名称</summary>
    string ProviderName { get; }

    /// <summary>
    /// 模型指纹：区分「哪个模型产出的向量空间」，是 embedding 缓存键的第二段。
    /// <para>同一文本在不同模型下的向量不可互换（spec §7.8.1）。本地替身实现可用
    /// 自身标识（如 <c>random-384</c>），真实路由实现用配置里的模型引用。</para>
    /// </summary>
    string ModelFingerprint { get; }
}

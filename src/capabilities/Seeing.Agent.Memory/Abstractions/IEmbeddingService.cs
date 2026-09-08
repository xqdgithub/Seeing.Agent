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
}

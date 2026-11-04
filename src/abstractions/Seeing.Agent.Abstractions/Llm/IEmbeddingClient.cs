namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 向量化端口（<see cref="LlmCapabilityKind.Embedding"/>）。
/// </summary>
public interface IEmbeddingClient : ILlmCapabilityClient
{
    /// <summary>把一批文本编码为向量。</summary>
    Task<EmbeddingResponse> EmbedAsync(
        EmbeddingRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default);
}

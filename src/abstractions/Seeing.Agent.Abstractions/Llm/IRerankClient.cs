namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 检索重排端口（<see cref="LlmCapabilityKind.Rerank"/>）。
/// 本次只出契约，无可跑实现（spec §7.11）。
/// </summary>
public interface IRerankClient : ILlmCapabilityClient
{
    /// <summary>按 query 对候选文档重排。</summary>
    Task<RerankResponse> RerankAsync(
        RerankRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default);
}

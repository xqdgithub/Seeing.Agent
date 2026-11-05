namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 结构化决策端口（<see cref="LlmCapabilityKind.Decision"/>，Jev 三原语）。
/// <para>原 <c>Seeing.Agent.SystemOne</c> 平行栈合流至此（spec §7.9）。</para>
/// <para><b>不含模型目录</b>：动态目录统一由 <see cref="IModelCatalogClient"/> 承担
/// （它返回 <see cref="ModelConfig"/>，直接进目录口径）。早期版本曾在此重复声明
/// <c>ListModelsAsync</c>，导致同一 wire 端点被两个接口各投影一次、且无任何消费者——
/// 与「同一事实不留三份数据」冲突，已删除。</para>
/// </summary>
public interface IDecisionClient : ILlmCapabilityClient
{
    /// <summary>一次评估多个 typed question。</summary>
    Task<DecisionResponse> EvaluateAsync(
        DecisionRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default);
}

namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 结构化决策端口（<see cref="LlmCapabilityKind.Decision"/>，Jev 三原语）。
/// <para>原 <c>Seeing.Agent.SystemOne</c> 平行栈合流至此（spec §7.9）。</para>
/// </summary>
public interface IDecisionClient : ILlmCapabilityClient
{
    /// <summary>一次评估多个 typed question。</summary>
    Task<DecisionResponse> EvaluateAsync(
        DecisionRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default);

    /// <summary>
    /// 动态模型目录。存在是为了保住 typesafe 的动态目录能力
    /// （返回项由调用方映射为 <see cref="ModelConfig"/>）。
    /// </summary>
    Task<IReadOnlyList<DecisionModel>> ListModelsAsync(CancellationToken ct = default);
}

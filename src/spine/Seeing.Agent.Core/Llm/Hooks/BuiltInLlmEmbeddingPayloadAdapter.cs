using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Core.Llm.Hooks;

/// <summary>
/// embedding 能力的 <c>llm.after_complete</c> 载荷适配器：产出向量条数与维度。
/// <para>不产出正文类键——向量本身进 Hook 载荷会把诊断变成内存泄漏（一次索引批的上万条 float[]）。</para>
/// </summary>
public sealed class BuiltInLlmEmbeddingPayloadAdapter : LlmHookPayloadAdapterBase
{
    /// <summary>
    /// 构造 embedding 载荷适配器。
    /// </summary>
    /// <param name="logger">可选日志。</param>
    public BuiltInLlmEmbeddingPayloadAdapter(ILogger<BuiltInLlmEmbeddingPayloadAdapter>? logger = null)
        : base(logger)
    {
    }

    /// <inheritdoc />
    public override LlmCapabilityKind Capability => LlmCapabilityKind.Embedding;

    /// <inheritdoc />
    protected override void AppendSpecialized(LlmPayloadContext context, IDictionary<string, object?> payload)
    {
        var response = context.Response as EmbeddingResponse;

        payload[HookDataContract.LlmAfterComplete.VectorCount.Name] = response?.Vectors.Count ?? 0;
        payload[HookDataContract.LlmAfterComplete.Dimensions.Name] = response?.Dimensions ?? 0;

        LlmHookPayloadKeys.FillUsageWhenAbsent(payload, response?.Usage);
    }
}

using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Core.Llm.Hooks;

/// <summary>
/// decision 能力的 <c>llm.after_complete</c> 载荷适配器：产出按问题名索引的应答集。
/// <para>应答原语（noul / choice / score）保持 DTO 原样，不在载荷里摊平——订阅方若要细读，
/// 它依赖的是 Abstractions 的 <see cref="DecisionAnswer"/>，不是键名约定。</para>
/// </summary>
public sealed class BuiltInLlmDecisionPayloadAdapter : LlmHookPayloadAdapterBase
{
    /// <summary>
    /// 构造 decision 载荷适配器。
    /// </summary>
    /// <param name="logger">可选日志。</param>
    public BuiltInLlmDecisionPayloadAdapter(ILogger<BuiltInLlmDecisionPayloadAdapter>? logger = null)
        : base(logger)
    {
    }

    /// <inheritdoc />
    public override LlmCapabilityKind Capability => LlmCapabilityKind.Decision;

    /// <inheritdoc />
    protected override void AppendSpecialized(LlmPayloadContext context, IDictionary<string, object?> payload)
    {
        var response = context.Response as DecisionResponse;

        payload[HookDataContract.LlmAfterComplete.Answers.Name] = response?.Answers;

        LlmHookPayloadKeys.FillUsageWhenAbsent(payload, response?.Usage);
    }
}

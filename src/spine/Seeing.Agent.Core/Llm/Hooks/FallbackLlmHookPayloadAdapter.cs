using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Core.Llm.Hooks;

/// <summary>
/// 通用兜底载荷适配器：只产出 <c>capability</c> / <c>provider</c> / <c>model</c> / <c>usage</c> /
/// <c>duration</c>（spec §7.5 / §7.6），<b>绝不抛异常</b>。
/// <para>Rerank / ImageGeneration / VideoGeneration 本次只出契约不实现（spec §7.11），
/// 它们的响应 DTO 无人认领；若让兜底去解释响应形状，等于把「诊断缺数据」升级成「调用炸掉」。
/// 因此本类不读 <see cref="LlmPayloadContext.Response"/>，连上下文本身为 null 也只产空值键。</para>
/// <para><see cref="Capability"/> 声明为 <see cref="LlmCapabilityKind.None"/>：兜底不认领任何
/// 具体能力，注册表在没有任何适配器命中时才落回它。</para>
/// </summary>
public sealed class FallbackLlmHookPayloadAdapter : ILlmHookPayloadAdapter
{
    /// <inheritdoc />
    public LlmCapabilityKind Capability => LlmCapabilityKind.None;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> Build(LlmPayloadContext context) =>
        LlmHookPayloadKeys.Common(context);
}

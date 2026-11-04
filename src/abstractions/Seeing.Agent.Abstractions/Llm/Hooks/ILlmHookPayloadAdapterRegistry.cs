namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// <see cref="ILlmHookPayloadAdapter"/> 的注册表。
/// <para>共存规则（spec §7.5 / 决议 3）：Core 内置五个适配器（Chat / Embedding / Decision /
/// SpeechSynthesis / SpeechTranscription）；插件注册同 <c>Capability</c> 的适配器则后注册胜；
/// 未实现的能力（Rerank / ImageGeneration / VideoGeneration）走通用兜底。</para>
/// </summary>
public interface ILlmHookPayloadAdapterRegistry
{
    /// <summary>登记适配器；同能力重复登记按「后注册胜」（与 Provider 注册表一致）。</summary>
    void Register(ILlmHookPayloadAdapter adapter);

    /// <summary>注销适配器；成功返回 true。</summary>
    bool Unregister(ILlmHookPayloadAdapter adapter);

    /// <summary>
    /// 按能力解析适配器；未注册任何适配器时返回通用兜底，<b>绝不返回 null、绝不抛异常</b>。
    /// <para>兜底不抛是刻意的：非 chat 能力本次只出契约不实现，若它们的调用让 Hook 爆炸，
    /// 等于把诊断功能变成故障源。</para>
    /// </summary>
    ILlmHookPayloadAdapter Resolve(LlmCapabilityKind capability);
}

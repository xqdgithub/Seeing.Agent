using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Core.Llm.Hooks;

/// <summary>
/// <see cref="ILlmHookPayloadAdapter"/> 的注册表：按能力精确匹配，未命中落回兜底。
/// <para><b>共存规则（spec §7.5 / 决议 3）</b>：Core 内置五个适配器构成基线；插件注册同
/// <see cref="ILlmHookPayloadAdapter.Capability"/> 的适配器时<b>后注册胜</b>（与 Provider 注册表
/// 「最后 wins + warning」一致，覆盖一定留下 Warning，不做静默覆盖）；
/// 未实现的能力（Rerank / ImageGeneration / VideoGeneration）走 <see cref="FallbackLlmHookPayloadAdapter"/>。</para>
/// <para>构造顺序上有讲究：先吃 DI 里登记的适配器（保持 DI 登记顺序），再补齐缺失的内置基线。
/// 这样"插件覆盖 chat"生效、而"宿主忘了登记五个内置"也不会让载荷退化成兜底。</para>
/// </summary>
public sealed class LlmHookPayloadAdapterRegistry : ILlmHookPayloadAdapterRegistry
{
    private readonly ConcurrentDictionary<LlmCapabilityKind, ILlmHookPayloadAdapter> _adapters = new();
    private readonly ILogger<LlmHookPayloadAdapterRegistry>? _logger;
    private readonly ILlmHookPayloadAdapter _fallback = new FallbackLlmHookPayloadAdapter();

    /// <summary>
    /// 构造注册表。
    /// </summary>
    /// <param name="adapters">
    /// DI 中已登记的适配器（含插件的覆盖项）；按登记顺序依次生效，同能力后者胜。
    /// 为 null 或空时只装配内置基线。
    /// </param>
    /// <param name="logger">可选日志；覆盖发生时打 Warning。</param>
    public LlmHookPayloadAdapterRegistry(
        IEnumerable<ILlmHookPayloadAdapter>? adapters = null,
        ILogger<LlmHookPayloadAdapterRegistry>? logger = null)
    {
        _logger = logger;

        if (adapters is not null)
        {
            foreach (var adapter in adapters)
            {
                Register(adapter);
            }
        }

        foreach (var builtIn in BuiltInAdapters())
        {
            if (!_adapters.ContainsKey(builtIn.Capability))
            {
                _adapters[builtIn.Capability] = builtIn;
            }
        }
    }

    /// <inheritdoc />
    public void Register(ILlmHookPayloadAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        if (_adapters.TryGetValue(adapter.Capability, out var existing) && !ReferenceEquals(existing, adapter))
        {
            _logger?.LogWarning(
                "能力 {Capability} 的 llm.after_complete 载荷适配器被覆盖（后注册胜）: {Old} -> {New}",
                adapter.Capability,
                existing.GetType().Name,
                adapter.GetType().Name);
        }

        _adapters[adapter.Capability] = adapter;
    }

    /// <inheritdoc />
    public bool Unregister(ILlmHookPayloadAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        // 只摘掉「当前生效的那一个」：被同名后来者覆盖时不应误删胜出者
        return _adapters.TryGetValue(adapter.Capability, out var current)
            && ReferenceEquals(current, adapter)
            && _adapters.TryRemove(new KeyValuePair<LlmCapabilityKind, ILlmHookPayloadAdapter>(adapter.Capability, adapter));
    }

    /// <inheritdoc />
    /// <remarks>
    /// 绝不返回 null、绝不抛异常：拿不到适配器就落兜底，让订阅方至少看到
    /// <c>capability</c> / <c>provider</c> / <c>model</c> / <c>usage</c> / <c>duration</c>。
    /// </remarks>
    public ILlmHookPayloadAdapter Resolve(LlmCapabilityKind capability) =>
        _adapters.TryGetValue(capability, out var adapter) ? adapter : _fallback;

    /// <summary>
    /// 内置五个适配器（Chat / Embedding / Decision / SpeechSynthesis / SpeechTranscription），
    /// 与 spec §7.5 的共存表一一对应。
    /// </summary>
    private static ILlmHookPayloadAdapter[] BuiltInAdapters() =>
    [
        new BuiltInLlmChatPayloadAdapter(),
        new BuiltInLlmEmbeddingPayloadAdapter(),
        new BuiltInLlmDecisionPayloadAdapter(),
        new BuiltInLlmSpeechSynthesisPayloadAdapter(),
        new BuiltInLlmSpeechTranscriptionPayloadAdapter(),
    ];
}

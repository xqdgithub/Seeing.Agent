namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 模型与客户端的配对出口：把「显式 modelId + 请求的能力端口」解析成一个可用客户端。
/// <para><b>只接受显式 modelId</b>：本接口不提供"未指定时取目录首个"的重载，Core 也不内置任何
/// 回落策略（spec §13 原则 1）。默认值的选择归调用方（能力包）。</para>
/// <para>校验顺序固定五层，失败抛 <see cref="LlmCapabilityMismatchException"/>（spec §7.4）：
/// ①模型存在 → ②类型声明含请求能力 → ③Speech 按 <c>Modalities</c> 消歧 →
/// ④Provider 声明该能力 → ⑤工厂确实产出了客户端。</para>
/// <para><b>常规单次业务调用请用 <see cref="ILlmInvoker"/>，不要直接用本接口</b>：
/// 只有 Invoker 会触发 <c>llm.params</c> / <c>llm.headers</c> / <c>llm.message</c> /
/// <c>llm.after_complete</c> / <c>llm.on_error</c>、合并出站头、计算耗时。
/// 直接 Resolve 的调用在观测面上是隐形的。</para>
/// <para>适用直接调用本接口的场景（spec §7.4.1）：</para>
/// <list type="bullet">
/// <item>一次解析多次调用（如 video <c>Submit</c> + N 次 <c>Poll</c>，避免记 N 次完成）。
/// <b>注意：video 目前只出契约、无实现，此条为前瞻约定。</b></item>
/// <item>框架内部装配（Invoker 自身、<c>IModelConfigManager.TryResolveDefault</c>、Provider 层）</item>
/// </list>
/// <para><b>连接测试探针不在此列</b>：探针应走 <see cref="ILlmInvoker"/> 且置
/// <see cref="LlmInvocationOptions.EmitHooks"/>=<c>false</c>——这样既有五层校验与重试，
/// 又不产生 Hook 副作用。见 <c>EmbeddingConnectionTester</c>。</para>
/// <para>判别口诀：<b>这次调用该不该被观测成「一次 LLM 调用」？</b>该 → Invoker（默认开 Hook）；
/// 不该但需要校验与重试 → Invoker + <c>EmitHooks=false</c>；一次解析多次调用 → 本接口。</para>
/// </summary>
public interface ILlmRouter
{
    /// <summary>
    /// 解析指定模型上承担 <typeparamref name="TClient"/> 能力的客户端。
    /// <para>若这是常规业务调用，请改用 <see cref="ILlmInvoker.InvokeAsync{TClient,TResponse}"/>，
    /// 否则 Hook 与出站头都不会生效。</para>
    /// </summary>
    /// <exception cref="LlmCapabilityMismatchException">五层校验任一层不通过。</exception>
    TClient Resolve<TClient>(string modelId)
        where TClient : class, ILlmCapabilityClient;

    /// <summary>
    /// 不抛异常版解析：失败时返回 <c>false</c> 并给出可读原因（供 UI 与启动诊断使用）。
    /// </summary>
    bool TryResolve<TClient>(string modelId, out TClient? client, out string? reason)
        where TClient : class, ILlmCapabilityClient;
}

namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// LLM 调用横切层：所有能力（chat / embedding / decision / speech…）的统一调用管道。
/// <para>Router 只负责解析，本层才是在非 chat 路径上触发 Hook、合并出站头、计算耗时的位置。
/// 固定流程（spec §7.5）：</para>
/// <code>
/// Resolve&lt;TClient&gt;(modelId)                 ← 五层校验
///   → 构造 LlmCallContext（SessionId / CorrelationId）
///   → llm.params（Blocking）+ llm.headers（Blocking）
///   → call() / stream()
///   → llm.message（Parallel）
///   → llm.after_complete（FireAndForget，载荷经 ILlmHookPayloadAdapter 组装）
///   → 异常时 llm.on_error（FireAndForget）后原样抛出
/// </code>
/// <para>重试装饰器在客户端内部（工厂应用），本层在其外层 → 重试发生在一次 Hook 周期之内，
/// <c>llm.on_error</c> 只在重试耗尽后触发一次。</para>
/// </summary>
public interface ILlmInvoker
{
    /// <summary>解析 → 触发前置 Hook → 调用 → 触发完成 Hook，返回客户端的原始响应。</summary>
    Task<TResponse> InvokeAsync<TClient, TResponse>(
        string modelId,
        Func<TClient, LlmCallContext, CancellationToken, Task<TResponse>> call,
        LlmInvocationOptions? options = null,
        CancellationToken cancellationToken = default)
        where TClient : class, ILlmCapabilityClient;

    /// <summary>流式版本：终态 Hook 在流结束时以累计用量触发。</summary>
    IAsyncEnumerable<TChunk> StreamAsync<TClient, TChunk>(
        string modelId,
        Func<TClient, LlmCallContext, CancellationToken, IAsyncEnumerable<TChunk>> stream,
        LlmInvocationOptions? options = null,
        CancellationToken cancellationToken = default)
        where TClient : class, ILlmCapabilityClient;
}

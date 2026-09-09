namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 出站请求信封：协议客户端在发送前交给拦截器塑形。
/// </summary>
public sealed class LlmOutboundRequest
{
    public required string ProviderId { get; init; }
    public required string ProviderType { get; init; }
    public LlmCallContext? Call { get; init; }

    /// <summary>可变头袋；合并到本次 HttpRequestMessage。</summary>
    public IDictionary<string, string> Headers { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Provider 作用域的出站拦截器（品牌网关塑形等）。
/// </summary>
public interface ILlmCallInterceptor
{
    int Order { get; }
    bool AppliesTo(string providerId, string providerType);
    void OnSending(LlmOutboundRequest request);
}

/// <summary>
/// 拦截器注册表：按 Order 排序，按 AppliesTo 过滤。
/// </summary>
public interface ILlmCallInterceptorRegistry
{
    void Register(ILlmCallInterceptor interceptor);
    bool Unregister(ILlmCallInterceptor interceptor);
    IReadOnlyList<ILlmCallInterceptor> Resolve(string providerId, string providerType);
}

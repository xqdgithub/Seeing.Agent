namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 协议无关的出站管道：静态头 → Call.ExtraHeaders → 拦截器 → 写入本次 HttpRequestMessage。
/// </summary>
public static class OutboundPipeline
{
    public static void Apply(
        HttpRequestMessage message,
        IReadOnlyDictionary<string, string>? staticHeaders,
        LlmCallContext? call,
        IReadOnlyList<ILlmCallInterceptor> interceptors,
        string providerId,
        string providerType)
    {
        ArgumentNullException.ThrowIfNull(message);

        var envelope = new LlmOutboundRequest
        {
            ProviderId = providerId,
            ProviderType = providerType,
            Call = call
        };

        if (staticHeaders is not null)
        {
            foreach (var (key, value) in staticHeaders)
                envelope.Headers[key] = value;
        }

        if (call?.ExtraHeaders is { Count: > 0 })
        {
            foreach (var (key, value) in call.ExtraHeaders)
                envelope.Headers[key] = value;
        }

        if (interceptors is { Count: > 0 })
        {
            foreach (var interceptor in interceptors)
                interceptor.OnSending(envelope);
        }

        foreach (var (key, value) in envelope.Headers)
        {
            message.Headers.Remove(key);
            message.Headers.TryAddWithoutValidation(key, value);
        }
    }
}

namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// LLM 调用上下文：携带调用期身份与可扩展的出站头/选项袋。
/// 由编排层在调用客户端前构造；不序列化进请求体，不污染 <see cref="ChatRequest"/>。
/// </summary>
public sealed class LlmCallContext
{
    public string? SessionId { get; init; }
    public string? ParentSessionId { get; init; }
    public string? CorrelationId { get; init; }

    /// <summary>通用出站头（如 chat.headers Hook 产出）。</summary>
    public Dictionary<string, string> ExtraHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>调用期 scratch（装饰器/拦截器可读写，如 retry.attempt）。</summary>
    public Dictionary<string, object?> Items { get; } = new(StringComparer.Ordinal);
}

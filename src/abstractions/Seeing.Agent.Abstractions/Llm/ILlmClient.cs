namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// LLM 客户端接口 - 只负责发送请求和接收响应。
/// 不负责模型定义、配置管理、消息转换等工作。
/// </summary>
public interface ILlmClient
{
    string ProviderId { get; }
    string ProviderType { get; }

    Task<ChatResponse> CompleteAsync(
        ChatRequest request,
        LlmCallContext? call = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
        ChatRequest request,
        LlmCallContext? call = null,
        CancellationToken cancellationToken = default);

    Task<bool> TestConnectionAsync(
        string modelId,
        LlmCallContext? call = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// LLM 客户端工厂接口
/// </summary>
public interface ILlmClientFactory
{
    ILlmClient Create(ProviderConfig config);
    IReadOnlySet<string> SupportedTypes { get; }
    bool SupportsType(string type);
}

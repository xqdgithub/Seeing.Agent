namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 对话补全端口（<see cref="LlmCapabilityKind.Chat"/>）。
/// </summary>
public interface IChatCompletionClient : ILlmCapabilityClient
{
    /// <summary>非流式补全。</summary>
    Task<ChatResponse> CompleteAsync(
        ChatRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default);

    /// <summary>流式补全：一律 <see cref="IAsyncEnumerable{T}"/>，禁止 async void。</summary>
    IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
        ChatRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default);
}

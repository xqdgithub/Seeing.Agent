using System.Runtime.CompilerServices;

namespace Seeing.Agent.Llm;

/// <summary>
/// LLM 客户端接口 - 只负责发送请求和接收响应
/// 不负责模型定义、配置管理、消息转换等工作
/// </summary>
public interface ILlmClient
{
    /// <summary>Provider ID</summary>
    string ProviderId { get; }

    /// <summary>Provider 类型</summary>
    ProviderType ProviderType { get; }

    /// <summary>
    /// 发送聊天补全请求
    /// </summary>
    /// <param name="request">聊天请求</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>聊天响应</returns>
    Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 发送流式聊天补全请求
    /// </summary>
    /// <param name="request">聊天请求</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>流式更新</returns>
    IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default);

    /// <summary>
    /// 测试连接是否可用
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>是否可用</returns>
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// LLM 客户端工厂接口
/// </summary>
public interface ILlmClientFactory
{
    /// <summary>
    /// 创建 LLM 客户端
    /// </summary>
    /// <param name="config">Provider 配置</param>
    /// <returns>LLM 客户端</returns>
    ILlmClient Create(ProviderConfig config);

    /// <summary>支持的 Provider 类型</summary>
    IReadOnlyList<ProviderType> SupportedTypes { get; }

    /// <summary>
    /// 检查是否支持指定类型
    /// </summary>
    bool SupportsType(ProviderType type);
}
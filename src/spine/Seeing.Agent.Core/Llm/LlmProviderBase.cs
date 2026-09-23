using Seeing.Agent.Abstractions.Llm;
namespace Seeing.Agent.Core.Llm;

/// <summary>
/// LLM Provider 的基础实现。
/// </summary>
public abstract class LlmProviderBase : ILlmProvider
{
    /// <summary>
    /// Provider 唯一标识。
    /// </summary>
    public abstract string Id { get; }

    /// <summary>
    /// Provider 显示名称。
    /// </summary>
    public abstract string? Name { get; }

    /// <summary>
    /// 最大重试次数，默认 3。
    /// </summary>
    public virtual int MaxRetries => 3;

    /// <summary>
    /// 获取 Provider 的 LLM 客户端。
    /// </summary>
    public abstract ILlmClient GetClient();

    /// <summary>
    /// 异步获取 Provider 下的模型目录。
    /// </summary>
    public abstract Task<IReadOnlyList<ModelConfig>> GetModelsAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// 测试与指定模型的连通性，默认委托客户端实现。
    /// </summary>
    public virtual Task<bool> TestConnectionAsync(
        string modelId,
        CancellationToken cancellationToken)
        => GetClient().TestConnectionAsync(modelId, call: null, cancellationToken);

    /// <summary>
    /// 使用内置客户端工厂创建 Provider 客户端。
    /// </summary>
    protected static ILlmClient CreateBuiltInClient(
        ILlmClientFactory factory,
        ProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(config);
        return factory.Create(config);
    }

    /// <summary>
    /// 从已登记工厂中解析支持指定类型的首个工厂（同类型多工厂时 first wins）。
    /// </summary>
    protected static ILlmClientFactory RequireFactory(
        IEnumerable<ILlmClientFactory> factories,
        string type)
        => LlmClientFactoryResolver.Require(factories, type);
}

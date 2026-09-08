using Seeing.Agent.Abstractions.Llm;
namespace Seeing.Agent.Core.Llm;

/// <summary>
/// LLM Provider 的基础实现。
/// </summary>
public abstract class LlmProviderBase : ILlmProvider
{
    public abstract string Id { get; }

    public abstract string? Name { get; }

    public virtual int MaxRetries => 3;

    public abstract ILlmClient GetClient();

    public abstract Task<IReadOnlyList<ModelConfig>> GetModelsAsync(
        CancellationToken cancellationToken);

    public virtual Task<bool> TestConnectionAsync(
        string modelId,
        CancellationToken cancellationToken)
        => GetClient().TestConnectionAsync(modelId, cancellationToken);

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

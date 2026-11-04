using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Core.Llm;

/// <summary>
/// LLM Provider 的基础实现：把 <see cref="ILlmProvider"/> 的样板收敛到一处。
/// <para>能力是配置驱动的一等公民（spec §7.3）：<see cref="Protocol"/> 决定用什么协议说话，
/// <see cref="Capabilities"/> 是 <see cref="ProviderConfig.Capabilities"/> 的运行时投影，
/// 客户端不再自述能力（决议 15）。</para>
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
    /// 协议（<c>openai</c> / <c>openai-responses</c> / <c>anthropic</c> / <c>typesafe</c>）。
    /// <para>默认空串：派生类应给出自己的协议；配置驱动 Provider 取 <c>config.Type</c>。</para>
    /// </summary>
    public virtual string Protocol => string.Empty;

    /// <summary>
    /// 该 Provider 供出的能力位集。
    /// <para>默认只给 <see cref="LlmCapabilityKind.Chat"/>：与
    /// <see cref="ProviderCapabilityDefaults"/> 的缺省一致——不声明就没有 embedding。</para>
    /// </summary>
    public virtual LlmCapabilityKind Capabilities => LlmCapabilityKind.Chat;

    /// <summary>
    /// 最大重试次数，默认 3。
    /// </summary>
    public virtual int MaxRetries => 3;

    /// <summary>
    /// 按能力取客户端；不供出该能力返回 <c>null</c>（不抛，Review Focus #1）。
    /// </summary>
    public abstract ILlmCapabilityClient? GetClient(LlmCapabilityKind capability);

    /// <summary>
    /// 异步获取 Provider 下的模型目录。
    /// </summary>
    public abstract Task<IReadOnlyList<ModelConfig>> GetModelsAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// 测试指定模型在指定能力上的连通性（决议 9）：直接取该能力的客户端，不查目录、不猜能力。
    /// <para>Provider 不供出该能力时返回 <c>false</c>——「没这个能力」与「连通失败」在 UI 上同等表现，
    /// 但诊断应读 <see cref="ProviderInfo.Capabilities"/>，不要靠连接测试反推。</para>
    /// </summary>
    public virtual Task<bool> TestConnectionAsync(
        string modelId,
        LlmCapabilityKind capability,
        CancellationToken cancellationToken)
    {
        var client = GetClient(capability);
        return client is null
            ? Task.FromResult(false)
            : client.TestConnectionAsync(modelId, capability, call: null, cancellationToken);
    }

    /// <summary>
    /// 使用内置客户端工厂按「配置 + 能力」创建客户端；工厂不支持该组合时返回 <c>null</c>。
    /// <para>装饰器（重试等）在工厂内部应用，本方法只负责转调（spec §7.3）。</para>
    /// </summary>
    protected static ILlmCapabilityClient? CreateBuiltInClient(
        ILlmClientFactory factory,
        ProviderConfig config,
        LlmCapabilityKind capability)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(config);
        return factory.Create(config, capability);
    }

    /// <summary>
    /// 从已登记工厂中解析支持指定协议的首个工厂（同协议多工厂时 first wins）。
    /// </summary>
    protected static ILlmClientFactory RequireFactory(
        IEnumerable<ILlmClientFactory> factories,
        string protocol)
        => LlmClientFactoryResolver.Require(factories, protocol);
}

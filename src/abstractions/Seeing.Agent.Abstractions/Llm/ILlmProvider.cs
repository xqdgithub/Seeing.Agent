namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 提供 LLM 客户端与模型目录的 Provider 抽象。
/// <para>一个 Provider 按能力供出多个客户端（同 endpoint、同 key 可同时供 chat 与 embedding）。
/// <see cref="Capabilities"/> 是 <see cref="ProviderConfig.Capabilities"/> 的运行时投影——
/// 能力的唯一真相在配置，客户端不自述（spec §7.3 / 决议 15）。</para>
/// </summary>
public interface ILlmProvider
{
    string Id { get; }

    string? Name { get; }

    /// <summary>协议：<c>openai</c> | <c>openai-responses</c> | <c>anthropic</c> | <c>typesafe</c>。</summary>
    string Protocol { get; }

    /// <summary>由 <see cref="ProviderConfig.Capabilities"/> 解析而来的能力位集。</summary>
    LlmCapabilityKind Capabilities { get; }

    /// <summary>
    /// 按能力取客户端；Provider 不供出该能力时返回 <c>null</c>（不抛）。
    /// <para>同一能力在同一 Provider 内应返回同一实例（缓存键是 providerId + capability，不是 modelId）。</para>
    /// </summary>
    ILlmCapabilityClient? GetClient(LlmCapabilityKind capability);

    Task<IReadOnlyList<ModelConfig>> GetModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 测试指定模型在指定能力上的连通性（决议 9）。
    /// <para>Provider 不推导、不猜能力：直接 <c>GetClient(capability)</c>，不查目录。</para>
    /// </summary>
    Task<bool> TestConnectionAsync(
        string modelId,
        LlmCapabilityKind capability,
        CancellationToken cancellationToken = default);

    int MaxRetries { get; }
}

namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 已注册 Provider 的描述信息（诊断与 UI 投影）。
/// <para><see cref="Capabilities"/> 是配置能力的运行时视图：客户端不自述能力，
/// 要看"这个 Provider 到底供出什么"就读这里（决议 15）。</para>
/// </summary>
public sealed class ProviderInfo
{
    public required string Id { get; init; }

    public string? Name { get; init; }

    /// <summary>协议：<c>openai</c> | <c>openai-responses</c> | <c>anthropic</c> | <c>typesafe</c>。</summary>
    public string Protocol { get; init; } = string.Empty;

    /// <summary>该 Provider 供出的能力位集。</summary>
    public LlmCapabilityKind Capabilities { get; init; }

    public ProviderSource Source { get; init; }

    public string? OwnerExtensionId { get; init; }

    public int MaxRetries { get; init; }
}

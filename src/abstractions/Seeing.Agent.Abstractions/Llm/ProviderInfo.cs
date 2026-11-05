namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 已注册 Provider 的对外信息（诊断与 UI 投影）。
/// <para><see cref="Capabilities"/> 是 Provider 声明的能力位集；见 <see cref="ILlmProvider.Capabilities"/>。</para>
/// </summary>
public sealed class ProviderInfo
{
    public required string Id { get; init; }

    public string? Name { get; init; }

    /// <summary>协议：<c>openai</c> | <c>openai-responses</c> | <c>anthropic</c> | <c>typesafe</c>。</summary>
    public string Protocol { get; init; } = string.Empty;

    /// <summary>该 Provider 供出的能力位集。</summary>
    public LlmCapabilityKind Capabilities { get; init; }

    /// <summary>来源。详情见 <see cref="ProviderOrigin"/>。</summary>
    public ProviderOrigin Origin { get; init; }

    /// <summary>归属标识（扩展 id / 模块 id）；配置驱动为 <c>null</c>。</summary>
    public string? OwnerId { get; init; }

    public int MaxRetries { get; init; }
}

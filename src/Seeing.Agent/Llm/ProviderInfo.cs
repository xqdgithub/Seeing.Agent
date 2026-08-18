using Seeing.Agent.Abstractions.Llm;
namespace Seeing.Agent.Llm;

/// <summary>
/// 已注册 Provider 的描述信息。
/// </summary>
public sealed class ProviderInfo
{
    public required string Id { get; init; }

    public string? Name { get; init; }

    public ProviderSource Source { get; init; }

    public string? OwnerExtensionId { get; init; }

    public int MaxRetries { get; init; }
}

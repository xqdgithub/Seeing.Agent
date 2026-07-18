namespace Seeing.Agent.Llm;

/// <summary>
/// Provider 连接端点（窄 DTO，不含完整 ProviderConfig）。
/// </summary>
public sealed class ProviderEndpoint
{
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
}

/// <summary>
/// 按名称查找 Provider 端点 — Embedding 等场景使用。
/// </summary>
public interface IProviderEndpointLookup
{
    bool TryGet(string providerName, out ProviderEndpoint? endpoint);
}

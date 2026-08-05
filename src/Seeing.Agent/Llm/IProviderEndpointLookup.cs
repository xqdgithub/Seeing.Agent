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
/// Provider 可选暴露的连接端点信息，供 Embedding 等旁路服务使用。
/// </summary>
public interface IProviderEndpointInfo
{
    string? BaseUrl { get; }
    string? ApiKey { get; }
}

/// <summary>
/// 按名称查找 Provider 端点 — Embedding 等场景使用。
/// </summary>
public interface IProviderEndpointLookup
{
    bool TryGet(string providerName, out ProviderEndpoint? endpoint);
}

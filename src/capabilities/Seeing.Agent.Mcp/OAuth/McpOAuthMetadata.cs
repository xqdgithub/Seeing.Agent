namespace Seeing.Agent.Mcp.OAuth
{
    /// <summary>
    /// RFC 9728 受保护资源元数据（<c>/.well-known/oauth-protected-resource</c>）。
    /// </summary>
    /// <param name="AuthorizationServers">资源可接受的授权服务器 issuer 列表（按优先顺序）。</param>
    public sealed record OAuthProtectedResourceMetadata(
        IReadOnlyList<string> AuthorizationServers);

    /// <summary>
    /// RFC 8414 授权服务器元数据（<c>/.well-known/oauth-authorization-server</c>）。
    /// </summary>
    /// <param name="AuthorizationEndpoint">授权端点</param>
    /// <param name="TokenEndpoint">令牌端点</param>
    /// <param name="RegistrationEndpoint">动态客户端注册端点（RFC 7591，可选）</param>
    public sealed record OAuthAuthorizationServerMetadata(
        string? AuthorizationEndpoint,
        string? TokenEndpoint,
        string? RegistrationEndpoint);

    /// <summary>
    /// RFC 7591 动态客户端注册结果（按 server 维度缓存）。
    /// </summary>
    /// <param name="ClientId">客户端 ID</param>
    /// <param name="ClientSecret">客户端密钥（可选，服务端未返回时为 null）</param>
    public sealed record OAuthClientRegistration(
        string ClientId,
        string? ClientSecret);
}

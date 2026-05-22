namespace Seeing.Agent.MCP.OAuth
{
    /// <summary>
    /// MCP OAuth 配置
    /// </summary>
    public class McpOAuthConfig
    {
        /// <summary>客户端 ID（可选，某些服务器支持动态注册）</summary>
        public string? ClientId { get; set; }

        /// <summary>客户端密钥（可选）</summary>
        public string? ClientSecret { get; set; }

        /// <summary>授权范围</summary>
        public string? Scope { get; set; }

        /// <summary>重定向 URI（可选，使用默认端口）</summary>
        public string? RedirectUri { get; set; }

        /// <summary>是否禁用 OAuth</summary>
        public bool Disabled { get; set; }

        /// <summary>令牌端点 URL（可选，自动发现）</summary>
        public string? TokenEndpoint { get; set; }

        /// <summary>授权端点 URL（可选，自动发现）</summary>
        public string? AuthorizationEndpoint { get; set; }

        /// <summary>是否使用 PKCE</summary>
        public bool UsePkce { get; set; } = true;
    }
}

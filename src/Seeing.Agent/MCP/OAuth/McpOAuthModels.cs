namespace Seeing.Agent.MCP.OAuth
{
    /// <summary>
    /// OAuth 启动结果
    /// </summary>
    /// <param name="AuthorizationUrl">授权 URL，用户需在浏览器中访问</param>
    /// <param name="State">OAuth state 参数，用于验证回调</param>
    /// <param name="CallbackPort">回调服务器端口</param>
    /// <param name="CodeVerifier">PKCE code_verifier</param>
    public record OAuthStartResult(
        string AuthorizationUrl,
        string State,
        int CallbackPort,
        string CodeVerifier);

    /// <summary>
    /// OAuth 结果
    /// </summary>
    /// <param name="Success">是否成功</param>
    /// <param name="Status">认证状态</param>
    /// <param name="Error">错误信息</param>
    /// <param name="Token">令牌</param>
    public record OAuthResult(
        bool Success,
        McpAuthStatus Status = McpAuthStatus.Authenticated,
        string? Error = null,
        McpOAuthToken? Token = null);

    /// <summary>
    /// MCP 认证状态
    /// </summary>
    public enum McpAuthStatus
    {
        /// <summary>已认证</summary>
        Authenticated,

        /// <summary>令牌已过期</summary>
        Expired,

        /// <summary>未认证</summary>
        NotAuthenticated,

        /// <summary>需要客户端注册</summary>
        NeedsClientRegistration,

        /// <summary>需要授权</summary>
        NeedsAuthorization
    }
}

namespace Seeing.Agent.MCP.OAuth
{
    /// <summary>
    /// MCP OAuth 提供者接口
    /// <para>
    /// 为远程 MCP 服务器提供 OAuth 2.0 授权支持，使用 PKCE 流程。
    /// </para>
    /// </summary>
    public interface IMcpOAuthProvider
    {
        /// <summary>
        /// 启动 OAuth 授权流程
        /// </summary>
        /// <param name="mcpName">MCP 服务器名称</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>授权启动结果，包含授权 URL、state 和 code_verifier</returns>
        Task<OAuthStartResult> StartAuthAsync(
            string mcpName,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 完成 OAuth 授权
        /// </summary>
        /// <param name="mcpName">MCP 服务器名称</param>
        /// <param name="authorizationCode">授权码</param>
        /// <param name="state">OAuth state 参数</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>授权结果</returns>
        Task<OAuthResult> FinishAuthAsync(
            string mcpName,
            string authorizationCode,
            string state,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 使用存储的令牌进行认证
        /// </summary>
        /// <param name="mcpName">MCP 服务器名称</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>认证结果</returns>
        Task<OAuthResult> AuthenticateAsync(
            string mcpName,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 刷新访问令牌
        /// </summary>
        /// <param name="mcpName">MCP 服务器名称</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>刷新结果</returns>
        Task<OAuthResult> RefreshTokenAsync(
            string mcpName,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 移除 OAuth 认证
        /// </summary>
        /// <param name="mcpName">MCP 服务器名称</param>
        Task RemoveAuthAsync(string mcpName);

        /// <summary>
        /// 检查是否有存储的令牌
        /// </summary>
        /// <param name="mcpName">MCP 服务器名称</param>
        /// <returns>是否存在令牌</returns>
        Task<bool> HasStoredTokensAsync(string mcpName);

        /// <summary>
        /// 获取认证状态
        /// </summary>
        /// <param name="mcpName">MCP 服务器名称</param>
        /// <returns>认证状态</returns>
        Task<McpAuthStatus> GetAuthStatusAsync(string mcpName);
    }
}

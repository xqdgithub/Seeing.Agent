namespace Seeing.Agent.Mcp.OAuth
{
    /// <summary>
    /// MCP OAuth 授权编排端口 — 驱动完整授权闭环（启动 → 打开浏览器 → 等待回调 → 交换令牌）。
    /// </summary>
    public interface IMcpOAuthAuthorizer
    {
        /// <summary>
        /// 发起并完成指定 MCP 服务器的 OAuth 授权。
        /// <para>
        /// 失败（未配置 OAuth / 无法打开浏览器 / 回调超时 / 交换失败）时返回
        /// <see cref="OAuthResult.Success"/>=false 并附明确错误，绝不伪造令牌。
        /// </para>
        /// </summary>
        /// <param name="mcpName">MCP 服务器名称</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task<OAuthResult> AuthorizeAsync(string mcpName, CancellationToken cancellationToken = default);
    }
}

using Seeing.Agent.Abstractions.Mcp;

namespace Seeing.Agent.Mcp.OAuth
{
    /// <summary>
    /// MCP OAuth 连接前预处理端口 — 在建立连接前确认授权状态。
    /// <para>
    /// OAuth 启用且无有效令牌（缺失/过期且刷新失败）时：
    /// 配置 <see cref="Seeing.Agent.Abstractions.Mcp.OAuth.McpOAuthConfig.AutoAuthorize"/>=true
    /// 则触发完整授权流；否则返回明确可操作提示（请运行 /mcp-auth）。
    /// 非交互宿主下默认开关关闭，绝不自动打开浏览器阻塞。
    /// </para>
    /// </summary>
    public interface IMcpOAuthConnectionPreparer
    {
        /// <summary>
        /// 确保指定 MCP 服务器在连接前具备有效授权。
        /// </summary>
        /// <param name="mcpName">MCP 服务器名称</param>
        /// <param name="config">服务器配置（含 OAuth 配置）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>授权结果；Success=false 时连接应中止并报告明确原因</returns>
        Task<OAuthResult> EnsureAuthorizedAsync(
            string mcpName,
            McpServerConfig config,
            CancellationToken cancellationToken = default);
    }
}

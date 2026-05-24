namespace Seeing.Agent.MCP.OAuth
{
    /// <summary>
    /// MCP OAuth 异常
    /// </summary>
    public class McpOAuthException : Exception
    {
        /// <summary>MCP 服务器名称</summary>
        public string? McpName { get; }

        /// <summary>认证状态</summary>
        public McpAuthStatus? Status { get; }

        /// <summary>
        /// 创建 MCP OAuth 异常
        /// </summary>
        public McpOAuthException(string message) : base(message) { }

        /// <summary>
        /// 创建 MCP OAuth 异常
        /// </summary>
        public McpOAuthException(string message, Exception inner) : base(message, inner) { }

        /// <summary>
        /// 创建 MCP OAuth 异常
        /// </summary>
        public McpOAuthException(string message, string mcpName, McpAuthStatus? status = null)
            : base(message)
        {
            McpName = mcpName;
            Status = status;
        }
    }
}

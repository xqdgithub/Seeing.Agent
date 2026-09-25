namespace Seeing.Agent.Mcp.OAuth
{
    /// <summary>
    /// OAuth 本地回调服务器抽象，便于测试替换。
    /// </summary>
    public interface IMcpOAuthCallbackServer
    {
        /// <summary>确保回调服务器运行，返回监听端口。</summary>
        Task<int> EnsureRunningAsync();

        /// <summary>获取回调 URL（http://localhost:{port}/callback）。</summary>
        string GetCallbackUrl();

        /// <summary>等待浏览器回调，返回授权码与 state；超时抛 <see cref="TimeoutException"/>。</summary>
        Task<(string Code, string State)> WaitForCallbackAsync(TimeSpan timeout);
    }
}

namespace Seeing.Agent.Mcp.OAuth
{
    /// <summary>
    /// OAuth 本地回调服务器抽象，便于测试替换。
    /// </summary>
    public interface IMcpOAuthCallbackServer
    {
        /// <summary>确保回调服务器运行，返回监听端口（单端口，仅绑定一次）。</summary>
        Task<int> EnsureRunningAsync();

        /// <summary>获取回调 URL（http://localhost:{port}/callback）。</summary>
        string GetCallbackUrl();

        /// <summary>
        /// 等待指定 <paramref name="state"/> 的回调，返回授权码与 state；超时抛 <see cref="TimeoutException"/>。
        /// <para>支持同进程并发授权：按 state 索引等待，回调按 state 分发；无匹配 state 的回调被拒绝。</para>
        /// </summary>
        /// <param name="state">本次授权流程生成并随回调带回的 state。</param>
        /// <param name="timeout">等待超时。</param>
        Task<(string Code, string State)> WaitForCallbackAsync(string state, TimeSpan timeout);
    }
}

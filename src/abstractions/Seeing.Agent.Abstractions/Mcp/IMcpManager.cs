namespace Seeing.Agent.Abstractions.Mcp;

public interface IMcpManager : IMcpStatusProvider, IMcpController, IMcpConfigManager, IAsyncDisposable
{
    Task InitializeAsync(IReadOnlyDictionary<string, McpServerConfig> configs, CancellationToken cancellationToken = default);
    Task ShutdownAsync(CancellationToken cancellationToken = default);

    /// <summary>卸载所有已注册的 MCP 工具（模块 Deactivate 时对称清理，覆盖非 Connected 状态的残留）。</summary>
    Task UnregisterAllToolsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 取得已连接 server 的协议会话面；未连接或无会话时返回 null。
    /// Resource / Prompt / Skills 工具经此取 Session，勿绕过 Manager。
    /// </summary>
    IMcpSession? TryGetSession(string serverName);

    event EventHandler<McpStatusChangedEventArgs>? StatusChanged;
}
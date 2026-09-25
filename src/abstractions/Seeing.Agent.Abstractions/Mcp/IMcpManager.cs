namespace Seeing.Agent.Abstractions.Mcp;

public interface IMcpManager : IMcpStatusProvider, IMcpController, IMcpConfigManager, IAsyncDisposable
{
    Task InitializeAsync(IReadOnlyDictionary<string, McpServerConfig> configs, CancellationToken cancellationToken = default);
    Task ShutdownAsync(CancellationToken cancellationToken = default);

    /// <summary>卸载所有已注册的 MCP 工具（模块 Deactivate 时对称清理，覆盖非 Connected 状态的残留）。</summary>
    Task UnregisterAllToolsAsync(CancellationToken cancellationToken = default);

    event EventHandler<McpStatusChangedEventArgs>? StatusChanged;
}
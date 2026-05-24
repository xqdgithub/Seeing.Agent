namespace Seeing.Agent.MCP.Core;

public interface IMcpManager : IMcpStatusProvider, IMcpController, IMcpConfigManager, IAsyncDisposable
{
    Task InitializeAsync(IReadOnlyDictionary<string, McpServerConfig> configs, CancellationToken cancellationToken = default);
    Task ShutdownAsync(CancellationToken cancellationToken = default);
    event EventHandler<McpStatusChangedEventArgs>? StatusChanged;
}
namespace Seeing.Agent.MCP.Core;

public interface IMcpController
{
    Task<McpOperationResult> ConnectServerAsync(string name, CancellationToken cancellationToken = default);
    Task<McpOperationResult> DisconnectServerAsync(string name, CancellationToken cancellationToken = default);
    Task<McpOperationResult> ReconnectServerAsync(string name, CancellationToken cancellationToken = default);
    McpOperationResult PauseServer(string name);
    McpOperationResult ResumeServer(string name);
    int PauseAllServers();
    int ResumeAllServers();
    Task<bool> WaitForReadyAsync(string name, int timeoutMs = 30000, CancellationToken cancellationToken = default);
}
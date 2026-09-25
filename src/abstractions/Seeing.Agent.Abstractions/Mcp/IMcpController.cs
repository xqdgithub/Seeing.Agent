namespace Seeing.Agent.Abstractions.Mcp;

public interface IMcpController
{
    Task<McpOperationResult> ConnectServerAsync(string name, CancellationToken cancellationToken = default);
    Task<McpOperationResult> DisconnectServerAsync(string name, CancellationToken cancellationToken = default);
    Task<McpOperationResult> ReconnectServerAsync(string name, CancellationToken cancellationToken = default);
    Task<McpOperationResult> PauseServerAsync(string name, CancellationToken cancellationToken = default);
    Task<McpOperationResult> ResumeServerAsync(string name, CancellationToken cancellationToken = default);
    Task<int> PauseAllServersAsync(CancellationToken cancellationToken = default);
    Task<int> ResumeAllServersAsync(CancellationToken cancellationToken = default);
    Task<bool> WaitForReadyAsync(string name, int timeoutMs = 30000, CancellationToken cancellationToken = default);
}
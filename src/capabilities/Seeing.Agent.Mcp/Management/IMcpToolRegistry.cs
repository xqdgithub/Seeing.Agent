namespace Seeing.Agent.Mcp.Management;

using Seeing.Agent.Mcp;
using Seeing.Agent.Abstractions.Mcp;
using System.Threading;
using System.Threading.Tasks;

public interface IMcpToolRegistry
{
    Task RegisterToolAsync(string serverName, string toolId, McpToolInfo toolInfo, CancellationToken ct = default);
    Task UnregisterToolAsync(string serverName, string toolId, CancellationToken ct = default);
    Task<McpOperationResult> UnregisterAllToolsAsync(string serverName);
    Task UpdateToolExecutorAsync(string serverName, Func<string, Dictionary<string, object?>, CancellationToken, Task<McpToolResult>> executor);
    bool HasTool(string toolId);
    int GetToolCount(string serverName);
}
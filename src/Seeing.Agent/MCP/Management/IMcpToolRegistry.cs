namespace Seeing.Agent.MCP.Management;

using System.Threading;
using System.Threading.Tasks;
using Seeing.Agent.MCP;
using Seeing.Agent.MCP.Core;

public interface IMcpToolRegistry
{
    Task RegisterToolAsync(string serverName, string toolId, McpToolInfo toolInfo, CancellationToken ct = default);
    Task UnregisterToolAsync(string serverName, string toolId, CancellationToken ct = default);
    Task<McpOperationResult> UnregisterAllToolsAsync(string serverName);
    void UpdateToolExecutor(string serverName, Func<string, Dictionary<string, object?>, Task<McpToolResult>> executor);
    bool HasTool(string toolId);
    int GetToolCount(string serverName);
}
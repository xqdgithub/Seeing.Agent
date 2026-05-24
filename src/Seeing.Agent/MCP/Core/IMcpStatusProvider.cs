namespace Seeing.Agent.MCP.Core;

public interface IMcpStatusProvider
{
    IReadOnlyDictionary<string, McpServerStatus> GetAllStatus();
    McpServerStatus? GetStatus(string name);
    IReadOnlyList<McpToolInfo> GetTools();
    bool IsAvailable(string name);
    IReadOnlyList<string> GetAvailableServers();
}

public sealed class McpToolInfo
{
    public string Name { get; init; } = string.Empty;
    public string ServerName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, object>? Schema { get; init; }
}
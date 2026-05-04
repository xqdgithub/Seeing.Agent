using System.Threading;
using System.Threading.Tasks;

namespace Seeing.Agent.MCP.Core;

public interface IMcpConfigManager
{
    Task<McpOperationResult> AddServerAsync(string name, McpServerConfig config, CancellationToken cancellationToken = default);
    Task<McpOperationResult> RemoveServerAsync(string name, CancellationToken cancellationToken = default);
    Task<McpOperationResult> UpdateConfigAsync(string name, McpServerConfig config, CancellationToken cancellationToken = default);
    Task<int> ReloadAllAsync(CancellationToken cancellationToken = default);
    (bool Valid, string? Error) ValidateConfig(string name, McpServerConfig config);
    McpServerConfig? GetConfig(string name);
    Task SaveConfigAsync(string? path = null, CancellationToken cancellationToken = default);
}
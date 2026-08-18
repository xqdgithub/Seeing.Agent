using Seeing.Agent.Abstractions.Mcp;

namespace Seeing.Agent.Abstractions.Extensions;

/// <summary>
/// 提供 MCP Server 配置的扩展
/// </summary>
public interface IMcpExtension
{
    /// <summary>提供的 MCP Server 配置</summary>
    IEnumerable<McpServerConfig> GetMcpServers();
}
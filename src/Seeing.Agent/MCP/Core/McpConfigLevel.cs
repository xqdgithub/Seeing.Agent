namespace Seeing.Agent.MCP.Core;

/// <summary>
/// 配置保存级别
/// </summary>
public enum McpConfigLevel
{
    /// <summary>项目级别（./.seeing/mcp.json）</summary>
    Project = 0,

    /// <summary>用户级别（~/.seeing/mcp.json）</summary>
    User = 1
}

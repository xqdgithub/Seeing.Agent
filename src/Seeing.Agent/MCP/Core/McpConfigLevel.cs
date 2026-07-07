using Seeing.Agent.Configuration;

namespace Seeing.Agent.MCP.Core;

/// <summary>
/// 配置保存级别
/// <para>
/// 已废弃：请使用 <see cref="Seeing.Agent.Configuration.ConfigLevel"/> 替代。
/// 此枚举将在 v2.0 版本中移除。
/// </para>
/// </summary>
[Obsolete("使用 Seeing.Agent.Configuration.ConfigLevel 替代。此枚举将在 v2.0 版本中移除。")]
public enum McpConfigLevel
{
    /// <summary>用户级别（~/.seeing/mcp.json）- 值已调整为与 ConfigLevel.User 一致</summary>
    User = 0,
    
    /// <summary>项目级别（./.seeing/mcp.json）- 值已调整为与 ConfigLevel.Project 一致</summary>
    Project = 1
}

/// <summary>
/// McpConfigLevel 转换扩展方法
/// </summary>
[Obsolete("使用 ConfigLevel 替代。此扩展方法将在 v2.0 版本中移除。")]
public static class McpConfigLevelExtensions
{
    /// <summary>转换为统一的 ConfigLevel</summary>
    public static ConfigLevel ToConfigLevel(this McpConfigLevel level)
        => (ConfigLevel)level;
    
    /// <summary>从 ConfigLevel 转换</summary>
    public static McpConfigLevel ToMcpConfigLevel(this ConfigLevel level)
        => (McpConfigLevel)level;
}
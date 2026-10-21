namespace Seeing.Agent.Abstractions.Tools;

/// <summary>
/// 工具 id / 命名空间约定常量（跨装配层共享，杜绝各处字面量漂移）。
/// </summary>
public static class ToolIdConventions
{
    /// <summary>MCP 工具 id 前缀（<c>mcp__{server}__{tool}</c>）。</summary>
    public const string McpPrefix = "mcp__";

    /// <summary>MCP id 内 server 与 tool 的分隔符（双下划线）。</summary>
    public const string McpSeparator = "__";

    /// <summary>检索元工具 id（分层暴露常驻内联、全量模式隐藏）。</summary>
    public const string SearchToolId = "tool_search";
}

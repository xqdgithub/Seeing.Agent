namespace Seeing.Agent.Mcp.Configuration;

/// <summary>
/// MCP 全局策略配置（可选节 SeeingAgent:Mcp；服务器列表仍在 mcp.json）。
/// </summary>
public sealed class McpOptions
{
    public const string SectionName = "Mcp";
    public const string ConfigurationSection = "SeeingAgent:Mcp";

    public int ConnectionTimeoutSeconds { get; set; } = 30;

    public int OperationTimeoutSeconds { get; set; } = 60;

    public int BackgroundCheckIntervalSeconds { get; set; } = 10;

    public int MaxConcurrentConnections { get; set; } = 3;

    public bool AutoStartOnAdd { get; set; } = true;
}
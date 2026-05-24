namespace Seeing.Agent.MCP.Core;

/// <summary>
/// MCP 连接运行时状态（与 <see cref="Seeing.Agent.MCP.McpServerConfig.Disabled"/> 配置标志配合使用）
/// </summary>
public enum McpConnectionState
{
    Pending,
    Connecting,
    Connected,
    Paused,
    Disabled,
    Reconnecting,
    Error,
    Removed
}

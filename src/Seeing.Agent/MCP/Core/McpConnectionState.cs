namespace Seeing.Agent.MCP.Core;

public enum McpConnectionState
{
    Pending,
    Connecting,
    Connected,
    Paused,
    Reconnecting,
    Error,
    Removed
}
namespace Seeing.Agent.Abstractions.Mcp;

public enum McpOperationType
{
    Connect,
    Disconnect,
    Reconnect,
    Pause,
    Resume,
    Add,
    Remove,
    UpdateConfig,
    Reload,
    Initialize,
    Shutdown
}
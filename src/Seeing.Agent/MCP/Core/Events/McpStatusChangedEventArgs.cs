namespace Seeing.Agent.MCP.Core;

public sealed class McpStatusChangedEventArgs : EventArgs
{
    public string ServerName { get; }
    public McpConnectionState PreviousState { get; }
    public McpConnectionState NewState { get; }
    public McpServerStatus Status { get; }
    public McpOperationType? TriggerOperation { get; }
    public McpErrorInfo? Error { get; }
    public DateTimeOffset Timestamp { get; }

    public McpStatusChangedEventArgs(
        string serverName,
        McpConnectionState previousState,
        McpConnectionState newState,
        McpServerStatus status,
        McpOperationType? triggerOperation = null,
        McpErrorInfo? error = null)
    {
        ServerName = serverName;
        PreviousState = previousState;
        NewState = newState;
        Status = status;
        TriggerOperation = triggerOperation;
        Error = error;
        Timestamp = DateTimeOffset.UtcNow;
    }
}
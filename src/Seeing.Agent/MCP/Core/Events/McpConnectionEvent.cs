namespace Seeing.Agent.MCP.Core;

public sealed class McpConnectionEvent
{
    public string ServerName { get; init; } = "";
    public McpConnectionState State { get; init; }
    public McpOperationType OperationType { get; init; }
    public McpErrorInfo? Error { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public TimeSpan Duration { get; init; }
    public string? Policy { get; init; }

    public static McpConnectionEvent Connected(
        string serverName,
        TimeSpan duration,
        string? policy = null)
        => new()
        {
            ServerName = serverName,
            State = McpConnectionState.Connected,
            OperationType = McpOperationType.Connect,
            Timestamp = DateTimeOffset.UtcNow,
            Duration = duration,
            Policy = policy
        };

    public static McpConnectionEvent Disconnected(
        string serverName,
        TimeSpan duration)
        => new()
        {
            ServerName = serverName,
            State = McpConnectionState.Pending,
            OperationType = McpOperationType.Disconnect,
            Timestamp = DateTimeOffset.UtcNow,
            Duration = duration
        };

    public static McpConnectionEvent Failed(
        string serverName,
        McpOperationType operationType,
        McpErrorInfo error,
        TimeSpan duration)
        => new()
        {
            ServerName = serverName,
            State = McpConnectionState.Error,
            OperationType = operationType,
            Error = error,
            Timestamp = DateTimeOffset.UtcNow,
            Duration = duration
        };

    public static McpConnectionEvent Reconnecting(
        string serverName,
        int attempt,
        string? policy = null)
        => new()
        {
            ServerName = serverName,
            State = McpConnectionState.Reconnecting,
            OperationType = McpOperationType.Reconnect,
            Timestamp = DateTimeOffset.UtcNow,
            Policy = policy
        };

    public static McpConnectionEvent Paused(
        string serverName)
        => new()
        {
            ServerName = serverName,
            State = McpConnectionState.Paused,
            OperationType = McpOperationType.Pause,
            Timestamp = DateTimeOffset.UtcNow
        };

    public static McpConnectionEvent Resumed(
        string serverName,
        TimeSpan duration)
        => new()
        {
            ServerName = serverName,
            State = McpConnectionState.Connected,
            OperationType = McpOperationType.Resume,
            Timestamp = DateTimeOffset.UtcNow,
            Duration = duration
        };
}
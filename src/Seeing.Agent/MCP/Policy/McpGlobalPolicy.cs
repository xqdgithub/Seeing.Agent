namespace Seeing.Agent.MCP.Policy;

public sealed class McpGlobalPolicy
{
    public McpReconnectionPolicy DefaultReconnectionPolicy { get; init; } = McpReconnectionPolicy.Default;
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan WaitForReadyTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxConcurrentConnections { get; init; } = 3;
    public bool AutoStartOnAdd { get; init; } = true;
    public TimeSpan BackgroundCheckInterval { get; init; } = TimeSpan.FromSeconds(10);
}
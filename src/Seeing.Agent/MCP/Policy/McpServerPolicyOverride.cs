namespace Seeing.Agent.MCP.Policy;

public sealed class McpServerPolicyOverride
{
    public string ServerName { get; init; } = string.Empty;
    public McpReconnectionPolicy? ReconnectionPolicy { get; init; }
    public TimeSpan? ConnectionTimeout { get; init; }
    public TimeSpan? OperationTimeout { get; init; }
    public bool? AutoStart { get; init; }
    public int? Priority { get; init; }

    public void ApplyTo(McpGlobalPolicy globalPolicy, out McpReconnectionPolicy reconnectionPolicy, out TimeSpan connectionTimeout, out TimeSpan operationTimeout, out bool autoStart, out int priority)
    {
        reconnectionPolicy = ReconnectionPolicy ?? globalPolicy.DefaultReconnectionPolicy;
        connectionTimeout = ConnectionTimeout ?? globalPolicy.ConnectionTimeout;
        operationTimeout = OperationTimeout ?? globalPolicy.OperationTimeout;
        autoStart = AutoStart ?? globalPolicy.AutoStartOnAdd;
        priority = Priority ?? 0;
    }
}
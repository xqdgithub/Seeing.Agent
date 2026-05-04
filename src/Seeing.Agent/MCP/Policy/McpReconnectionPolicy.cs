namespace Seeing.Agent.MCP.Policy;

public enum McpConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    Error,
    Reconnecting
}

public sealed class McpReconnectionPolicy
{
    public bool Enabled { get; init; } = true;
    public int MaxAttempts { get; init; } = 5;
    public TimeSpan InitialInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxInterval { get; init; } = TimeSpan.FromSeconds(60);
    public double BackoffMultiplier { get; init; } = 2.0;
    public bool ResetOnSuccess { get; init; } = true;
    public TimeSpan SuccessThreshold { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan CalculateInterval(int attempt)
    {
        var interval = InitialInterval * Math.Pow(BackoffMultiplier, attempt - 1);
        return interval > MaxInterval ? MaxInterval : interval;
    }

    public bool CanReconnect(int currentAttempts, McpConnectionState state)
        => Enabled && currentAttempts < MaxAttempts && state == McpConnectionState.Error;

    public static McpReconnectionPolicy Default => new();
    public static McpReconnectionPolicy Fast => new()
    {
        MaxAttempts = 10,
        InitialInterval = TimeSpan.FromSeconds(1),
        MaxInterval = TimeSpan.FromSeconds(30),
        BackoffMultiplier = 1.5
    };
    public static McpReconnectionPolicy Conservative => new()
    {
        MaxAttempts = 3,
        InitialInterval = TimeSpan.FromSeconds(5),
        MaxInterval = TimeSpan.FromSeconds(120),
        BackoffMultiplier = 3.0
    };
}
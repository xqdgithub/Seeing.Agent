namespace Seeing.Agent.App.Execution;

/// <summary>
/// Configuration options for the execution engine.
/// </summary>
public class ExecutionOptions
{
    /// <summary>
    /// Maximum number of concurrent executions across all sessions.
    /// Set to -1 for unlimited (default).
    /// </summary>
    public int MaxConcurrentExecutions { get; set; } = -1;

    /// <summary>
    /// Size of the event buffer per session (for reconnection recovery).
    /// Default: 100 events.
    /// </summary>
    public int EventBufferSize { get; set; } = 100;

    /// <summary>
    /// Maximum number of execution history entries to keep per session.
    /// Default: 100 entries.
    /// </summary>
    public int ExecutionHistoryLimit { get; set; } = 100;

    /// <summary>
    /// Time after which an idle session's resources are cleaned up.
    /// A session is idle when it has no active execution and no queued tasks.
    /// Default: 30 minutes.
    /// </summary>
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Interval for the cleanup timer.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum number of queued executions per session.
    /// Default: 10.
    /// </summary>
    public int MaxQueueSizePerSession { get; set; } = 10;
}
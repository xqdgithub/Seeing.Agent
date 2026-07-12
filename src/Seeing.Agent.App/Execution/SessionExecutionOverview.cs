namespace Seeing.Agent.App.Execution;

/// <summary>
/// Overview of execution state for a session.
/// </summary>
public class SessionExecutionOverview
{
    /// <summary>
    /// The currently executing or pending execution, if any.
    /// </summary>
    public ExecutionRecord? CurrentExecution { get; set; }

    /// <summary>
    /// Number of executions waiting in the queue.
    /// </summary>
    public int QueueLength { get; set; }

    /// <summary>
    /// List of queued executions (in order).
    /// </summary>
    public IReadOnlyList<ExecutionRecord> QueuedExecutions { get; set; } = new List<ExecutionRecord>();

    /// <summary>
    /// Whether there is an active execution (running or pending).
    /// </summary>
    public bool HasActiveExecution => CurrentExecution != null &&
        (CurrentExecution.Status == ExecutionStatus.Running ||
         CurrentExecution.Status == ExecutionStatus.Pending);

    /// <summary>
    /// Whether there are executions waiting in the queue.
    /// </summary>
    public bool HasQueuedExecutions => QueueLength > 0;
}
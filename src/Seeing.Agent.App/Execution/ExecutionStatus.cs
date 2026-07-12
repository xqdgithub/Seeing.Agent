namespace Seeing.Agent.App.Execution;

/// <summary>
/// Represents the status of an execution request.
/// </summary>
public enum ExecutionStatus
{
    /// <summary>
    /// The execution is waiting to be processed.
    /// </summary>
    Pending,

    /// <summary>
    /// The execution is queued behind another execution for the same session.
    /// </summary>
    Queued,

    /// <summary>
    /// The execution is currently running.
    /// </summary>
    Running,

    /// <summary>
    /// The execution completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// The execution failed with an error.
    /// </summary>
    Failed,

    /// <summary>
    /// The execution was cancelled.
    /// </summary>
    Cancelled
}

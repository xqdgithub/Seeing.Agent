namespace Seeing.Agent.App.Execution;

/// <summary>
/// Result of submitting an execution request.
/// </summary>
public class ExecutionSubmitResult
{
    /// <summary>
    /// Whether the submission was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// The unique identifier for this execution.
    /// </summary>
    public string? ExecutionId { get; set; }

    /// <summary>
    /// The current status of the execution.
    /// </summary>
    public ExecutionStatus Status { get; set; }

    /// <summary>
    /// Position in the queue (0 = executing immediately, >0 = queued).
    /// </summary>
    public int QueuePosition { get; set; }

    /// <summary>
    /// Human-readable message about the submission.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Error message if submission failed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Creates a successful result for an immediately executing request.
    /// </summary>
    public static ExecutionSubmitResult Succeeded(string executionId) => new()
    {
        Success = true,
        ExecutionId = executionId,
        Status = ExecutionStatus.Pending,
        QueuePosition = 0,
        Message = "Execution submitted successfully"
    };

    /// <summary>
    /// Creates a successful result for a queued request.
    /// </summary>
    public static ExecutionSubmitResult Queued(string executionId, int position) => new()
    {
        Success = true,
        ExecutionId = executionId,
        Status = ExecutionStatus.Queued,
        QueuePosition = position,
        Message = $"Execution queued at position {position}"
    };

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static ExecutionSubmitResult Failed(string error) => new()
    {
        Success = false,
        Status = ExecutionStatus.Failed,
        Error = error
    };
}
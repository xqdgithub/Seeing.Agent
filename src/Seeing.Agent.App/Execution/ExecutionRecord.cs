using Seeing.Agent.App.Models;

namespace Seeing.Agent.App.Execution;

/// <summary>
/// Represents a record of an execution request, tracking its state throughout its lifecycle.
/// </summary>
public class ExecutionRecord
{
    /// <summary>
    /// Unique identifier for this execution.
    /// </summary>
    public string ExecutionId { get; set; } = string.Empty;

    /// <summary>
    /// The session this execution belongs to.
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// Current status of the execution.
    /// </summary>
    public ExecutionStatus Status { get; set; } = ExecutionStatus.Pending;

    /// <summary>
    /// The input for this execution (user message, attachments).
    /// </summary>
    public ChatInput? Input { get; set; }

    /// <summary>
    /// The options for this execution (agent, model, etc.).
    /// </summary>
    public ChatOptions? Options { get; set; }

    /// <summary>
    /// When the execution was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the execution actually started processing.
    /// </summary>
    public DateTime StartedAt { get; set; }

    /// <summary>
    /// When the execution reached a terminal state.
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Error message if the execution failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// ID of the last processed message (for recovery purposes).
    /// </summary>
    public string? LastMessageId { get; set; }

    /// <summary>
    /// Number of messages processed during this execution.
    /// </summary>
    public int ProcessedMessageCount { get; set; }

    /// <summary>
    /// Position in the queue (0 = currently executing, >0 = queued).
    /// </summary>
    public int QueuePosition { get; set; }

    /// <summary>
    /// When this execution entered the queue (if it was queued).
    /// </summary>
    public DateTime? QueuedAt { get; set; }

    /// <summary>
    /// Whether this execution is in a terminal state.
    /// </summary>
    public bool IsTerminal => Status is ExecutionStatus.Completed 
        or ExecutionStatus.Failed 
        or ExecutionStatus.Cancelled;
}

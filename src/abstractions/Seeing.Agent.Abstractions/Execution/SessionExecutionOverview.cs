namespace Seeing.Agent.Abstractions.Execution;

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
    /// Whether there is an active execution (running or pending).
    /// </summary>
    public bool HasActiveExecution => CurrentExecution != null &&
        (CurrentExecution.Status == ExecutionStatus.Running ||
         CurrentExecution.Status == ExecutionStatus.Pending);

    /// <summary>该会话最近一轮执行记录；终态后保留至下一轮开始。空闲态终态判定的唯一来源。</summary>
    public ExecutionRecord? LastExecution { get; set; }

    /// <summary>会话当前未消费输入数（仅 UI 展示，不用于终态判定）。</summary>
    public int PendingInputCount { get; set; }
}

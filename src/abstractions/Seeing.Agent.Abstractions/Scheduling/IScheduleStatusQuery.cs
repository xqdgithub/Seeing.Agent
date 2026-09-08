namespace Seeing.Agent.Abstractions.Scheduling;

/// <summary>
/// 可选调度状态查询 — Gateway Admin 等宿主在未登记 Scheduler 时可缺失。
/// </summary>
public interface IScheduleStatusQuery
{
    /// <summary>调度器是否已启动。</summary>
    bool IsStarted { get; }

    /// <summary>列出全部任务状态快照。</summary>
    Task<IReadOnlyList<ScheduleJobStatusSnapshot>> GetAllJobStatusesAsync(CancellationToken ct = default);
}

/// <summary>任务状态快照（Admin / 跨包只读）。</summary>
public sealed class ScheduleJobStatusSnapshot
{
    public required string JobId { get; init; }
    public string? JobName { get; init; }
    public required string State { get; init; }
    public DateTime? PreviousFireTime { get; init; }
    public DateTime? NextFireTime { get; init; }
    public string? LastError { get; init; }
}

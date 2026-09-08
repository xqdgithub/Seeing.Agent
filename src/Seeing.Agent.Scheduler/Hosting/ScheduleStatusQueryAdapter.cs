using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Agent.Scheduler.Abstractions;

namespace Seeing.Agent.Scheduler.Hosting;

/// <summary>将 <see cref="IScheduleManager"/> 适配为跨包只读状态查询。</summary>
internal sealed class ScheduleStatusQueryAdapter : IScheduleStatusQuery
{
    private readonly IScheduleManager _manager;

    public ScheduleStatusQueryAdapter(IScheduleManager manager) => _manager = manager;

    public bool IsStarted => _manager.IsStarted;

    public async Task<IReadOnlyList<ScheduleJobStatusSnapshot>> GetAllJobStatusesAsync(CancellationToken ct = default)
    {
        var statuses = await _manager.GetAllJobStatusesAsync(ct).ConfigureAwait(false);
        return statuses.Select(s => new ScheduleJobStatusSnapshot
        {
            JobId = s.JobId,
            JobName = s.JobName,
            State = s.State.ToString(),
            PreviousFireTime = s.PreviousFireTime,
            NextFireTime = s.NextFireTime,
            LastError = s.LastError
        }).ToList();
    }
}

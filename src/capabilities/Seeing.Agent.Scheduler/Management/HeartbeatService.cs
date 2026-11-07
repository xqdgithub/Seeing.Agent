using Microsoft.Extensions.Logging;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.Scheduler.Management;

/// <summary>
/// 心跳上层门面。
/// <para>
/// 所有写操作都转调 <see cref="ScheduleManager"/>：本类不持有 Quartz 实例、不写 jobs.json，
/// 因此不会出现「状态、触发、历史各一套」的第二真相源。
/// </para>
/// <para>
/// 只读侧（历史）直接读仓储：执行历史本来就按 jobId 分文件存放，没必要为读取绕一圈管理器。
/// </para>
/// </summary>
public sealed class HeartbeatService : IHeartbeatService
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;

    private readonly ScheduleManager _manager;
    private readonly IScheduleRepository _repository;
    private readonly ILogger<HeartbeatService> _logger;

    public HeartbeatService(
        ScheduleManager manager,
        IScheduleRepository repository,
        ILogger<HeartbeatService> logger)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<HeartbeatSnapshot> GetAsync(CancellationToken ct = default)
    {
        var spec = _manager.HeartbeatSpec();
        if (spec is null)
        {
            // 未配置：状态一律为 null，页面据此展示空状态而不是伪造的下次触发时间。
            return new HeartbeatSnapshot(null, null, null, _manager.IsStarted);
        }

        var status = await _manager.GetJobStatusAsync(SchedulerConstants.HeartbeatJobId, ct)
            .ConfigureAwait(false);
        var last = await _repository
            .GetHistoryAsync(SchedulerConstants.HeartbeatJobId, 1, 0, ct)
            .ConfigureAwait(false);

        return new HeartbeatSnapshot(spec, status, last.FirstOrDefault(), _manager.IsStarted);
    }

    /// <inheritdoc />
    public async Task<HeartbeatSnapshot> UpdateAsync(HeartbeatUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        // 在副本上应用并校验：校验失败时槽位与调度器都保持原样。
        var spec = _manager.HeartbeatSpec() ?? CreateDefaultSpec();
        Apply(spec, update);
        Validate(spec);

        await _manager.SaveHeartbeatAsync(spec, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "心跳配置已保存 (Enabled={Enabled}, Every={Every})",
            spec.Intent == ScheduleIntent.Active,
            spec.Schedule.Every);

        return await GetAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<TriggerResult> RunNowAsync(CancellationToken ct = default)
        => _manager.RunHeartbeatAsync(ct);

    /// <inheritdoc />
    public Task<bool> CancelAsync(CancellationToken ct = default)
        => _manager.CancelHeartbeatAsync(ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<JobExecutionRecord>> GetHistoryAsync(
        int limit,
        int offset = 0,
        CancellationToken ct = default)
        => _repository.GetHistoryAsync(
            SchedulerConstants.HeartbeatJobId,
            Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit),
            Math.Max(0, offset),
            ct);

    private static ScheduledJobSpec CreateDefaultSpec() => new()
    {
        Id = SchedulerConstants.HeartbeatJobId,
        Name = "心跳",
        Intent = ScheduleIntent.Disabled,
        TaskType = ScheduleTaskTypes.Agent,
        Schedule = new ScheduleSpec
        {
            Type = ScheduleTypes.Interval,
            Every = "6h",
            Timezone = TimeZoneInfo.Local.Id
        },
        Dispatch = new DispatchSpec { Target = new DispatchTarget() },
        Runtime = new JobRuntimeSpec()
    };

    private static void Apply(ScheduledJobSpec spec, HeartbeatUpdate update)
    {
        if (update.Enabled is { } enabled)
            spec.Intent = enabled ? ScheduleIntent.Active : ScheduleIntent.Disabled;

        if (update.Prompt is not null)
            spec.Prompt = update.Prompt;

        if (update.Every is not null)
            spec.Schedule.Every = update.Every.Trim();

        if (update.Windows is not null)
            spec.Schedule.Windows = update.Windows.Count == 0 ? null : update.Windows.ToList();

        if (update.Timezone is not null)
            spec.Schedule.Timezone = update.Timezone.Trim();

        if (update.SessionId is not null)
        {
            spec.Dispatch.Target.SessionId = string.IsNullOrWhiteSpace(update.SessionId)
                ? null
                : update.SessionId.Trim();
        }

        if (update.Conversation is not null)
        {
            spec.Dispatch.Target.Conversation = update.Conversation.IsComplete
                ? update.Conversation
                : null;
        }

        if (update.Agent is not null)
            spec.Agent = string.IsNullOrWhiteSpace(update.Agent) ? null : update.Agent.Trim();

        if (update.Model is not null)
            spec.Model = string.IsNullOrWhiteSpace(update.Model) ? null : update.Model.Trim();

        if (update.TimeoutSeconds is { } timeout)
            spec.Runtime.TimeoutSeconds = timeout;

        // 心跳只支持间隔 + 生效时段：拒绝 cron 表达式与一次性时间，写回时强制清空。
        spec.TaskType = ScheduleTaskTypes.Agent;
        spec.Schedule.Type = ScheduleTypes.Interval;
        spec.Schedule.Cron = null;
        spec.Schedule.RunAt = null;
    }

    private static void Validate(ScheduledJobSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Prompt))
            throw new ArgumentException("心跳 Prompt 不能为空。");

        if (string.IsNullOrWhiteSpace(spec.Schedule.Every))
            throw new ArgumentException("心跳间隔不能为空，例如 6h / 30m。");

        try
        {
            ScheduleExpressionParser.ParseInterval(spec.Schedule.Every);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException($"心跳间隔格式无效：{ex.Message}", ex);
        }

        if (!ScheduleWindowsValidator.ValidateForScheduleType(
                ScheduleTypes.Interval, spec.Schedule.Windows, out var windowsError))
        {
            throw new ArgumentException(windowsError ?? "生效时段无效。");
        }

        var hasSession = !string.IsNullOrWhiteSpace(spec.Dispatch?.Target?.SessionId);
        var hasConversation = spec.Dispatch?.Target?.Conversation is { IsComplete: true };
        if (!hasSession && !hasConversation)
            throw new ArgumentException("心跳需要一个目标会话或出站 conversation。");

        if (spec.Runtime.TimeoutSeconds <= 0)
            throw new ArgumentException("心跳超时必须大于 0 秒。");
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Core.Background;

/// <summary>
/// 后台活动会话面与管理面的唯一实现（同一个类实现
/// <see cref="IBackgroundActivityManager"/> 与 <see cref="IBackgroundActivityControl"/>）。
/// <para>
/// 不变量：
/// <list type="number">
/// <item>状态只在本类内变化；<c>Running</c> 迁移要求当前为 <c>Pending</c>。</item>
/// <item>终态只能提交一次（CAS），提交后 reporter 全部 no-op。</item>
/// <item>终态先落盘（<see cref="IBackgroundActivityStore.SaveAsync"/>）再发事件。</item>
/// <item>分支只看 <see cref="BackgroundActivitySnapshot.Capabilities"/>，不读 <c>Kind</c> / <c>Attributes</c>。</item>
/// </list>
/// </para>
/// <para>
/// 类型为 internal：构造参数含 internal 的存储端口；宿主只经两个公共接口解析（Core 内部注册）。
/// </para>
/// </summary>
internal sealed class BackgroundActivityManager : IBackgroundActivityManager, IBackgroundActivityControl
{
    private const string ActivityIdPrefix = "act_";

    /// <summary><c>activity.progress</c> 的最小事件间隔（spec §12.1：≥1s）。</summary>
    private static readonly TimeSpan s_progressEventInterval = TimeSpan.FromSeconds(1);

    private static readonly string s_hostInstanceId = BackgroundActivityHostIdentity.Current;

    private readonly ConcurrentDictionary<string, BackgroundActivityRecord> _activities = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LauncherRegistration> _launchers = new(StringComparer.Ordinal);

    /// <summary>正在注销的 kind（<see cref="_sync"/> 内维护）：阻止新活动在注销窗口逃逸。</summary>
    private readonly HashSet<string> _unregistering = new(StringComparer.Ordinal);

    /// <summary>所有状态变更与快照读取的唯一临界区；临界区内不得 <c>await</c>。</summary>
    private readonly object _sync = new();

    private readonly IExecutionEventPublisher _eventPublisher;
    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<BackgroundActivityOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly IBackgroundActivityStore _store;
    private readonly IActivityOutputStore _outputStore;
    private readonly IBackgroundActivityNotificationQueue _notifications;
    private readonly ILogger<BackgroundActivityManager> _logger;
    private readonly CancellationToken _hostStoppingToken;

    public BackgroundActivityManager(
        IExecutionEventPublisher eventPublisher,
        IServiceProvider services,
        IOptionsMonitor<BackgroundActivityOptions> options,
        TimeProvider timeProvider,
        IBackgroundActivityStore store,
        IActivityOutputStore outputStore,
        IBackgroundActivityNotificationQueue notifications,
        ILogger<BackgroundActivityManager> logger)
    {
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _outputStore = outputStore ?? throw new ArgumentNullException(nameof(outputStore));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // 宿主停止 token 是可选接缝：独立单测/嵌入场景取不到时为 None（只链超时）。
        _hostStoppingToken = _services.GetService<IHostApplicationLifetime>()?.ApplicationStopping
            ?? CancellationToken.None;
    }

    // ─── 会话面 ───────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<BackgroundActivityHandle> StartAsync(
        BackgroundActivityStartRequest request,
        string requesterSessionId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(requesterSessionId);

        if (!string.Equals(request.OwnerSessionId, requesterSessionId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                $"会话 '{requesterSessionId}' 不能为会话 '{request.OwnerSessionId}' 启动后台活动。");
        }

        return StartCoreAsync(request, ct);
    }

    /// <inheritdoc />
    public BackgroundActivitySnapshot? Get(string activityId, string requesterSessionId)
    {
        if (!TryGetReadable(activityId, requesterSessionId, out var record))
            return null;

        lock (_sync)
        {
            return record.Snapshot;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<BackgroundActivitySnapshot> List(BackgroundActivityQuery query, string requesterSessionId)
    {
        query ??= new BackgroundActivityQuery();
        ArgumentException.ThrowIfNullOrWhiteSpace(requesterSessionId);

        var result = new List<BackgroundActivitySnapshot>();
        lock (_sync)
        {
            foreach (var record in _activities.Values)
            {
                var snapshot = record.Snapshot;

                // List 不提供跨会话视图：他人活动既不返回也不报错。
                if (!string.Equals(snapshot.OwnerSessionId, requesterSessionId, StringComparison.Ordinal))
                    continue;

                if (query.Status is { } status && snapshot.Status != status)
                    continue;

                if (!string.IsNullOrEmpty(query.Kind) && !string.Equals(query.Kind, snapshot.Kind, StringComparison.Ordinal))
                    continue;

                if (!query.IncludeCompleted && IsTerminal(snapshot.Status))
                    continue;

                result.Add(snapshot);
            }
        }

        // 最新在前，再按配置截断：无上限的历史记录不得把一次 List 变成全量回放。
        result.Sort(static (left, right) => right.CreatedAt.CompareTo(left.CreatedAt));

        var limit = _options.CurrentValue.ListLimit;
        if (limit > 0 && result.Count > limit)
            result.RemoveRange(limit, result.Count - limit);

        return result;
    }

    /// <inheritdoc />
    public async Task<bool> CancelAsync(
        string activityId,
        BackgroundActivityCancelReason reason,
        string requesterSessionId,
        CancellationToken ct = default)
    {
        if (!TryGetOwned(activityId, requesterSessionId, out var root))
            return false;

        var cancelled = false;
        foreach (var record in CollectSubtree(root))
        {
            ct.ThrowIfCancellationRequested();

            // 根用调用方给的原因，子孙一律 CascadeFromParent（取消树语义）。
            var effectiveReason = ReferenceEquals(record, root)
                ? reason
                : BackgroundActivityCancelReason.CascadeFromParent;

            if (await CancelCoreAsync(record, effectiveReason).ConfigureAwait(false))
                cancelled = true;
        }

        return cancelled;
    }

    /// <inheritdoc />
    public async Task<BackgroundActivityWaitResult> WaitAsync(
        string activityId,
        TimeSpan timeout,
        string requesterSessionId,
        CancellationToken ct = default)
    {
        if (!TryGetOwned(activityId, requesterSessionId, out var record))
            return new BackgroundActivityWaitResult(BackgroundActivityWaitOutcome.TimedOut, null);

        // 「转后台」放行是逐次的：每个等待者持自己的信号，DetachAsync 只放行其中一个。
        // 转后台之后新的 activity_wait 重新入列，仍然等到终态（不再立刻 Detached）。
        var detach = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_sync)
        {
            // 终态提交与登记同临界区：迟到的登记不会挂到已清空的列表上（否则永远等不到结果）。
            if (record.TerminalCommitted)
                return new BackgroundActivityWaitResult(BackgroundActivityWaitOutcome.Completed, record.Snapshot);

            record.DetachWaiters.Add(detach);
        }

        try
        {
            var winner = await Task.WhenAny(record.TerminalSignal.Task, detach.Task)
                .WaitAsync(timeout, ct)
                .ConfigureAwait(false);

            // detach 信号被置 false 只发生在终态提交之后：仍按终态返回。
            if (!ReferenceEquals(winner, record.TerminalSignal.Task)
                && await ((Task<bool>)winner).ConfigureAwait(false))
            {
                return new BackgroundActivityWaitResult(
                    BackgroundActivityWaitOutcome.Detached, CurrentSnapshot(record));
            }

            var snapshot = await record.TerminalSignal.Task.ConfigureAwait(false);
            return new BackgroundActivityWaitResult(BackgroundActivityWaitOutcome.Completed, snapshot);
        }
        catch (TimeoutException)
        {
            return new BackgroundActivityWaitResult(BackgroundActivityWaitOutcome.TimedOut, CurrentSnapshot(record));
        }
        catch (OperationCanceledException)
        {
            return new BackgroundActivityWaitResult(BackgroundActivityWaitOutcome.CallerCancelled, CurrentSnapshot(record));
        }
        finally
        {
            // 正常完成时列表已被终态清空或在 DetachAsync 里被摘除；这里只兜住超时/取消两类退出。
            lock (_sync)
            {
                record.DetachWaiters.Remove(detach);
            }
        }
    }

    /// <inheritdoc />
    public async Task<BackgroundActivityOutputPage> ReadOutputAsync(
        string activityId,
        long offset,
        int maxBytes,
        string requesterSessionId,
        CancellationToken ct = default)
    {
        if (!TryGetOwned(activityId, requesterSessionId, out _))
            return new BackgroundActivityOutputPage(string.Empty, offset, EndOfStream: true, Truncated: false);

        return await _outputStore.ReadAsync(activityId, offset, maxBytes, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void MarkResultObserved(string activityId, string requesterSessionId)
    {
        if (!TryGetOwned(activityId, requesterSessionId, out var record))
            return;

        lock (_sync)
        {
            if (record.Snapshot.ResultObservedAt is not null)
                return;

            // 状态迁移与入链同一临界区：落盘顺序 == Seq 顺序（R1）。
            var snapshot = record.Snapshot with
            {
                Seq = record.Snapshot.Seq + 1,
                ResultObservedAt = _timeProvider.GetLocalNow()
            };
            record.Snapshot = snapshot;
            _ = EnqueuePersist(record, snapshot, eventType: null);
        }
    }

    // ─── 系统面 ───────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<BackgroundActivityHandle> StartAsSystemAsync(
        BackgroundActivityStartRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerSessionId);

        return StartCoreAsync(request, ct);
    }

    /// <inheritdoc />
    public Task<BackgroundActivityHandle> TrackAsync(
        BackgroundActivityTrackRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerSessionId);

        var snapshot = new BackgroundActivitySnapshot
        {
            Id = NewActivityId(),
            Kind = request.Kind,
            Title = request.Title,
            Origin = request.Origin,
            WorkspaceRoot = ResolveWorkspaceRoot(),
            HostInstanceId = s_hostInstanceId,
            CreatedAt = _timeProvider.GetLocalNow(),
            OwnerSessionId = request.OwnerSessionId,
            OwnerExecutionId = request.OwnerExecutionId,
            ParentActivityId = request.ParentActivityId,
            TargetSessionId = request.TargetSessionId,
            Status = BackgroundActivityStatus.Pending,
            Seq = 0,
            Capabilities = request.Capabilities,
            NotifyPolicy = request.NotifyPolicy,
            Timeout = null,
            OutputLimitBytes = _options.CurrentValue.DefaultOutputLimitBytes,
            Visibility = BackgroundActivityVisibility.Session,
            Lifetime = BackgroundActivityLifetime.Host,
            Attributes = CopyAttributes(request.Attributes)
        };

        var record = CreateRecord(snapshot, launcherKind: null, dedupeKey: null, timeout: null);

        lock (_sync)
        {
            _activities[record.Handle.ActivityId] = record;
        }

        // 登记型活动由宿主直接提供完成信号或自行上报终态，manager 只置 Running 并观察完成。
        TryCommitRunning(record);

        if (request.Completion is { } completion)
            _ = ObserveCompletionAsync(record, completion);

        if (_hostStoppingToken.IsCancellationRequested)
            OnHostStopping(record);

        return Task.FromResult(record.Handle);
    }

    /// <inheritdoc />
    public void RegisterLauncher(string moduleId, IBackgroundActivityLauncher launcher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentException.ThrowIfNullOrWhiteSpace(launcher.Kind);

        var registration = new LauncherRegistration(moduleId, launcher);
        if (!_launchers.TryAdd(launcher.Kind, registration))
        {
            var existing = _launchers.TryGetValue(launcher.Kind, out var current) ? current.ModuleId : "?";
            throw new InvalidOperationException(
                $"后台活动 kind '{launcher.Kind}' 已由模块 '{existing}' 登记，不能重复登记。");
        }
    }

    /// <inheritdoc />
    public async Task<int> UnregisterLauncherAsync(string kind, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        // R3：先置「注销中」并保留 launcher 引用——取消时要能通知到它，且并发 Start 不得穿过。
        LauncherRegistration? registration = null;
        var targets = new List<BackgroundActivityRecord>();
        lock (_sync)
        {
            _unregistering.Add(kind);
            _launchers.TryGetValue(kind, out registration);

            foreach (var record in _activities.Values)
            {
                if (record.TerminalCommitted)
                    continue;

                if (string.Equals(record.LauncherKind, kind, StringComparison.Ordinal))
                    targets.Add(record);
            }
        }

        var cancelled = 0;
        try
        {
            foreach (var record in targets)
            {
                ct.ThrowIfCancellationRequested();
                if (await CancelCoreAsync(record, BackgroundActivityCancelReason.ModuleDeactivated, registration)
                        .ConfigureAwait(false))
                {
                    cancelled++;
                }
            }

            // 必须在返回前等到终态（spec §9.5：模块停用不得留活口）。
            var terminalWaits = targets.Select(static record => (Task)record.TerminalSignal.Task).ToArray();
            if (terminalWaits.Length > 0)
                await Task.WhenAll(terminalWaits).WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            // 最后才摘登记并解除闸门：中途失败也必须恢复，否则该 kind 永久不可用。
            lock (_sync)
            {
                _unregistering.Remove(kind);

                if (registration is not null
                    && _launchers.TryGetValue(kind, out var current)
                    && ReferenceEquals(current, registration))
                {
                    _launchers.TryRemove(kind, out _);
                }
            }
        }

        return cancelled;
    }

    /// <inheritdoc />
    public Task<int> CancelBySessionAsync(
        string sessionId,
        BackgroundActivityCancelReason reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return CancelMatchingAsync(
            snapshot => string.Equals(snapshot.OwnerSessionId, sessionId, StringComparison.Ordinal),
            reason,
            ct);
    }

    /// <inheritdoc />
    public Task<int> CancelByExecutionAsync(
        string executionId,
        BackgroundActivityCancelReason reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        return CancelMatchingAsync(
            snapshot => string.Equals(snapshot.OwnerExecutionId, executionId, StringComparison.Ordinal),
            reason,
            ct);
    }

    /// <inheritdoc />
    public Task<int> CancelAllAsync(BackgroundActivityCancelReason reason, CancellationToken ct = default)
        => CancelMatchingAsync(static _ => true, reason, ct);

    /// <inheritdoc />
    public async Task<int> ReconcileOrphanedActivitiesAsync(CancellationToken ct = default)
    {
        IReadOnlyList<BackgroundActivitySnapshot> candidates;
        try
        {
            candidates = await _store.LoadAllAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 扫描失败不阻断宿主启动 / 工作区切换：下次触发点再试。
            _logger.LogWarning(ex, "后台活动孤儿扫描读取失败，本次跳过");
            return 0;
        }

        var reconciled = 0;
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            // 只收敛本轮仍记为 Running 的活动；终态记录是权威事实，绝不改写。
            if (candidate.Status != BackgroundActivityStatus.Running)
                continue;

            if (BackgroundActivityHostIdentity.IsAlive(candidate.HostInstanceId))
                continue;

            var completedAt = _timeProvider.GetLocalNow();
            try
            {
                // 重读校验在存储的路径锁内完成：别的实例刚提交的终态不会被旧快照盖回去。
                var replaced = await _store.ReplaceIfUnchangedAsync(
                        candidate.Id,
                        BackgroundActivityStatus.Running,
                        candidate.HostInstanceId,
                        current => current with
                        {
                            Status = BackgroundActivityStatus.Interrupted,
                            Seq = current.Seq + 1,
                            CompletedAt = completedAt,
                            CancelReason = BackgroundActivityCancelReason.HostShutdown
                        })
                    .ConfigureAwait(false);

                if (replaced)
                    reconciled++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "后台活动孤儿收敛失败 ActivityId={ActivityId} HostInstanceId={HostInstanceId}",
                    candidate.Id,
                    candidate.HostInstanceId);
            }
        }

        if (reconciled > 0)
            _logger.LogInformation("后台活动孤儿收敛完成 Count={Count}", reconciled);

        // 只落记录：不入 outbox、不发事件（spec §16）。
        return reconciled;
    }

    /// <inheritdoc />
    public async Task<int> PruneAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        var targets = new List<BackgroundActivityRecord>();
        lock (_sync)
        {
            foreach (var record in _activities.Values)
            {
                if (!record.TerminalCommitted)
                    continue;

                // 只回收终态：CompletedAt 缺失的终态记录（异常路径）视为不可判龄，保留。
                if (record.Snapshot.CompletedAt is not { } completedAt || completedAt >= cutoff)
                    continue;

                targets.Add(record);
            }
        }

        var pruned = 0;
        foreach (var record in targets)
        {
            ct.ThrowIfCancellationRequested();

            var activityId = record.Handle.ActivityId;
            try
            {
                // 先删落盘（输出 + 快照），最后才从内存摘除：
                // 删除失败时记录仍在表里，下一轮扫描还能重试（反过来会变成看不见的孤儿文件）。
                await _outputStore.DeleteAsync(activityId).ConfigureAwait(false);
                await _store.DeleteAsync(activityId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "后台活动回收失败，保留待下次重试 ActivityId={ActivityId} CompletedAt={CompletedAt}",
                    activityId,
                    record.Snapshot.CompletedAt);
                continue;
            }

            lock (_sync)
            {
                _activities.TryRemove(activityId, out _);
            }

            pruned++;
        }

        if (pruned > 0)
            _logger.LogInformation("后台活动回收完成 Count={Count} Cutoff={Cutoff}", pruned, cutoff);

        return pruned;
    }

    /// <inheritdoc />
    public Task<bool> DetachAsync(string activityId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activityId);
        ct.ThrowIfCancellationRequested();

        // 系统面操作：不做 requester 归属校验（本接口不暴露给能力包）。
        if (!_activities.TryGetValue(activityId, out var record))
            return Task.FromResult(false);

        TaskCompletionSource<bool>? released = null;
        lock (_sync)
        {
            // 终态之后等待者已经或即将拿到终态，再放行只会吞掉终态语义。
            if (record.TerminalCommitted || IsTerminal(record.Snapshot.Status))
                return Task.FromResult(false);

            // 没有前台等待者时此调用无语义（调用方拿到 false 说明没人可放）。
            if (record.DetachWaiters.Count == 0)
                return Task.FromResult(false);

            // 只放行一个等待者：其余等待者（以及转后台之后新的 activity_wait）继续等终态。
            released = record.DetachWaiters[0];
            record.DetachWaiters.RemoveAt(0);
        }

        // 只放等待者：活动状态、落盘、事件一律不动（spec §23.2）。
        // 上面的早退路径都已返回，执行到这里 released 必非 null。
        var detached = released!.TrySetResult(true);

        if (detached)
            _logger.LogInformation("后台活动已转后台 ActivityId={ActivityId}", activityId);

        return Task.FromResult(detached);
    }

    // ─── reporter 回写（internal：只被 BackgroundActivityReporter 调用） ────────

    internal void ReportStarted(string activityId)
    {
        if (!_activities.TryGetValue(activityId, out var record))
            return;

        TryCommitRunning(record);
    }

    internal void ReportProgress(string activityId, BackgroundActivityProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        BackgroundActivitySnapshot snapshot;
        bool publish;
        lock (_sync)
        {
            if (!_activities.TryGetValue(activityId, out var record) || record.TerminalCommitted)
                return;

            var now = _timeProvider.GetUtcNow();
            snapshot = record.Snapshot with
            {
                Seq = record.Snapshot.Seq + 1,
                Progress = progress,
                LastHeartbeatAt = now
            };
            record.Snapshot = snapshot;

            // Progress 不落盘，只发节流事件（≥1s）。
            publish = record.LastProgressEventAt is null || now - record.LastProgressEventAt >= s_progressEventInterval;
            if (publish)
                record.LastProgressEventAt = now;
        }

        if (publish)
            Publish(snapshot, ActivityEventTypes.Progress);
    }

    internal void Heartbeat(string activityId)
    {
        lock (_sync)
        {
            if (!_activities.TryGetValue(activityId, out var record) || record.TerminalCommitted)
                return;

            record.Snapshot = record.Snapshot with
            {
                Seq = record.Snapshot.Seq + 1,
                LastHeartbeatAt = _timeProvider.GetUtcNow()
            };
        }
    }

    internal async Task AppendOutputAsync(string activityId, ReadOnlyMemory<byte> chunk, CancellationToken ct)
    {
        if (chunk.IsEmpty)
            return;

        if (!_activities.TryGetValue(activityId, out var record) || IsTerminalCommitted(record))
            return;

        ActivityOutputAppendResult appended;
        try
        {
            appended = await _outputStore.AppendAsync(activityId, chunk, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 输出写盘失败只影响这一段输出：活动主流程（进程/终态）不受牵连。
            _logger.LogWarning(ex, "后台活动输出追加失败 ActivityId={ActivityId}", activityId);
            return;
        }

        BackgroundActivitySnapshot snapshot;
        string? outputTail = null;
        lock (_sync)
        {
            // 写入期间可能已提交终态：终态快照是权威事实，不再回填统计（也绝不改 Seq）。
            if (record.TerminalCommitted)
                return;

            // 只把真正落盘的字节喂给实时尾部（截断后溢出部分不在文件里，也不该出现在 UI）。
            var previousBytes = record.Snapshot.OutputBytes ?? 0;
            var written = (int)Math.Clamp(appended.TotalBytes - previousBytes, 0, chunk.Length);
            if (written > 0)
                record.AppendOutputTail(chunk.Span[..written]);

            // 统计来自存储的实际落盘结果（截断后不再按传入字节累计）；不额外落盘——
            // 下一次状态迁移（含终态）会把这两个字段一并写出。
            snapshot = record.Snapshot with
            {
                Seq = record.Snapshot.Seq + 1,
                OutputBytes = appended.TotalBytes,
                OutputTruncated = appended.Truncated
            };
            record.Snapshot = snapshot;

            // 节流：与 ReportProgress 共用同一时钟，同一活动 ≥1s 才发一次 progress。
            // progress 不落盘、不新增历史，只带实时输出尾部。
            var now = _timeProvider.GetUtcNow();
            if (record.LastProgressEventAt is null || now - record.LastProgressEventAt >= s_progressEventInterval)
            {
                record.LastProgressEventAt = now;
                outputTail = record.OutputTail;
            }
        }

        if (outputTail is not null)
            Publish(snapshot, ActivityEventTypes.Progress, outputTail);
    }

    internal async Task CompleteAsync(
        string activityId,
        BackgroundActivityResult result,
        CancellationToken ct = default)
    {
        if (!_activities.TryGetValue(activityId, out var record))
            return;

        await FinalizeRecordAsync(record, BackgroundActivityStatus.Succeeded, cancelReason: null, result, ct)
            .ConfigureAwait(false);
    }

    internal async Task FailAsync(
        string activityId,
        string errorCode,
        string errorMessage,
        CancellationToken ct = default)
    {
        if (!_activities.TryGetValue(activityId, out var record))
            return;

        await FinalizeRecordAsync(
                record,
                BackgroundActivityStatus.Failed,
                cancelReason: null,
                new BackgroundActivityResult(
                    BackgroundActivityResultKind.None,
                    ArtifactPath: null,
                    ResultSessionId: null,
                    ExitCode: null,
                    errorCode,
                    errorMessage),
                ct)
            .ConfigureAwait(false);
    }

    // ─── 启动 ─────────────────────────────────────────────────────────────────

    private async Task<BackgroundActivityHandle> StartCoreAsync(
        BackgroundActivityStartRequest request,
        CancellationToken ct)
    {
        if (!_launchers.TryGetValue(request.Kind, out var registration))
            throw new InvalidOperationException($"未登记的后台活动 kind '{request.Kind}'。");

        if (!string.IsNullOrEmpty(request.DedupeKey))
        {
            var existing = FindDedupeCandidate(request);
            if (existing is not null)
            {
                if (request.DedupeStrategy == BackgroundActivityDedupeStrategy.Reject)
                {
                    // 复用语义：返回在途活动的 handle，并显式标记「没有新建」，
                    // 让工具能如实告诉模型「命令没有重新执行」。
                    _logger.LogDebug(
                        "后台活动命中去重，返回已有活动 DedupeKey={DedupeKey} ActivityId={ActivityId}",
                        request.DedupeKey,
                        existing.Handle.ActivityId);
                    return existing.Handle with { IsExisting = true };
                }

                await CancelCoreAsync(existing, BackgroundActivityCancelReason.Superseded).ConfigureAwait(false);
            }
        }

        var options = _options.CurrentValue;
        var effectiveTimeout = ResolveTimeout(request.Timeout, options);
        var snapshot = new BackgroundActivitySnapshot
        {
            Id = NewActivityId(),
            Kind = request.Kind,
            Title = request.Title,
            OriginToolCallId = request.OriginToolCallId,
            Origin = request.Origin,
            WorkspaceRoot = ResolveWorkspaceRoot(),
            HostInstanceId = s_hostInstanceId,
            CreatedAt = _timeProvider.GetLocalNow(),
            OwnerSessionId = request.OwnerSessionId,
            OwnerExecutionId = request.OwnerExecutionId,
            ParentActivityId = request.ParentActivityId,
            TargetSessionId = request.TargetSessionId,
            Status = BackgroundActivityStatus.Pending,
            Seq = 0,
            Capabilities = registration.Launcher.Capabilities,
            NotifyPolicy = request.NotifyPolicy,
            Timeout = effectiveTimeout,
            OutputLimitBytes = request.OutputLimitBytes ?? options.DefaultOutputLimitBytes,
            Visibility = request.Visibility,
            Lifetime = request.Lifetime,
            Attributes = CopyAttributes(request.Attributes)
        };

        var record = CreateRecord(snapshot, registration.Launcher.Kind, request.DedupeKey, effectiveTimeout);

        var admitted = false;
        string? rejectReason = null;
        lock (_sync)
        {
            // R3：注销窗口内不得放行新活动（launcher 引用即将/已经消失，放行会成为无人取消的孤儿）。
            if (_unregistering.Contains(request.Kind))
            {
                rejectReason = $"后台活动 kind '{request.Kind}' 正在注销，不能启动新活动。";
            }
            else if (request.Origin == BackgroundActivityOrigin.ToolForeground)
            {
                // 前台命令的并发由执行链自身控制：它既不占后台配额，也不被后台配额拒绝
                // （否则并行的前台 bash / 在跑的后台任务会把普通命令挡死）。
                _activities[record.Handle.ActivityId] = record;
                admitted = true;
            }
            else
            {
                var limit = Math.Max(1, options.MaxConcurrentPerSession);
                if (CountBackgroundActivitiesLocked(request.OwnerSessionId) >= limit)
                {
                    _logger.LogWarning(
                        "会话后台活动并发已达上限 SessionId={SessionId} Limit={Limit} Kind={Kind}",
                        request.OwnerSessionId,
                        limit,
                        request.Kind);
                    rejectReason = $"会话后台活动并发已达上限 {limit}（SessionId={request.OwnerSessionId}）。";
                }
                else
                {
                    _activities[record.Handle.ActivityId] = record;
                    admitted = true;
                }
            }
        }

        if (!admitted)
        {
            ReleaseRejectedRecord(record);
            throw new InvalidOperationException(rejectReason!);
        }

        // 每个活动自带输出上限（请求值优先，否则配置默认值）：截断口径与快照里声明的一致。
        _outputStore.Configure(record.Handle.ActivityId, snapshot.OutputLimitBytes);

        // 宿主已在停止过程中：不启动 launcher，直接落 Interrupted（不留 Pending 僵尸）。
        if (_hostStoppingToken.IsCancellationRequested)
        {
            OnHostStopping(record);
            return record.Handle;
        }

        try
        {
            using var launchCts = CancellationTokenSource.CreateLinkedTokenSource(record.Cts.Token, ct);
            var launchResult = await registration.Launcher
                .LaunchAsync(
                    new BackgroundActivityLaunchContext(record.Handle, request, request.WorkingDirectory),
                    launchCts.Token)
                .ConfigureAwait(false);

            // Running 由 manager 置：launcher 已先上报终态时这里丢弃。
            TryCommitRunning(record);
            ObserveExecution(record, launchResult.Execution);
        }
        catch (OperationCanceledException)
        {
            // 启动期被取消（宿主停止 / 超时 / 调用方 ct）：不留 Pending 僵尸。
            await FinalizeRecordAsync(
                    record,
                    BackgroundActivityStatus.Cancelled,
                    BackgroundActivityCancelReason.User,
                    result: null)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "后台活动启动失败 ActivityId={ActivityId} Kind={Kind}", record.Handle.ActivityId, request.Kind);
            await FinalizeRecordAsync(
                    record,
                    BackgroundActivityStatus.Failed,
                    cancelReason: null,
                    new BackgroundActivityResult(
                        BackgroundActivityResultKind.None,
                        ArtifactPath: null,
                        ResultSessionId: null,
                        ExitCode: null,
                        "launch_failed",
                        ex.Message))
                .ConfigureAwait(false);
            throw;
        }

        if (_hostStoppingToken.IsCancellationRequested)
            OnHostStopping(record);

        return record.Handle;
    }

    // ─── 状态迁移 ─────────────────────────────────────────────────────────────

    /// <summary>Pending → Running；终态已提交或当前非 Pending 时丢弃。</summary>
    private bool TryCommitRunning(BackgroundActivityRecord record)
    {
        lock (_sync)
        {
            if (record.TerminalCommitted || record.Snapshot.Status != BackgroundActivityStatus.Pending)
                return false;

            var snapshot = record.Snapshot with
            {
                Status = BackgroundActivityStatus.Running,
                Seq = record.Snapshot.Seq + 1,
                StartedAt = record.Snapshot.StartedAt ?? _timeProvider.GetLocalNow()
            };
            record.Snapshot = snapshot;

            // 迁移与入链同一临界区（R1）：落盘顺序严格等于 Seq 顺序，过期快照不会覆盖终态。
            _ = EnqueuePersist(record, snapshot, ActivityEventTypes.Started);
        }

        return true;
    }

    /// <summary>
    /// 终态 CAS：先到先得，只提交一次（锁内完成，不 <c>await</c>）。返回 <see langword="true"/> 表示本次调用赢得终态。
    /// <para>
    /// 锁内：内存 CAS + 入链（保证终态落盘排在此前所有状态之后）；
    /// 锁外：释放定时器 / 宿主登记、取消并释放 CTS、唤醒等待方。
    /// </para>
    /// </summary>
    private bool TryCommitTerminalState(
        BackgroundActivityRecord record,
        BackgroundActivityStatus status,
        BackgroundActivityCancelReason? cancelReason,
        BackgroundActivityResult? result,
        out BackgroundActivitySnapshot snapshot,
        out Task<bool> persistence)
    {
        ITimer? timer;
        CancellationTokenRegistration hostRegistration;
        CancellationTokenSource cts;
        TaskCompletionSource<bool>[] detachWaiters;
        lock (_sync)
        {
            if (record.TerminalCommitted)
            {
                snapshot = record.Snapshot;
                persistence = Task.FromResult(true);
                return false;
            }

            record.TerminalCommitted = true;
            snapshot = record.Snapshot with
            {
                Status = status,
                Seq = record.Snapshot.Seq + 1,
                CompletedAt = _timeProvider.GetLocalNow(),
                CancelReason = cancelReason,
                Result = result ?? record.Snapshot.Result
            };
            record.Snapshot = snapshot;

            // 同一临界区内入链：任何在途 Running/MarkResultObserved 快照都排在终态之前。
            persistence = EnqueuePersist(record, snapshot, ResolveEventType(status));

            timer = record.TimeoutTimer;
            record.TimeoutTimer = null;
            hostRegistration = record.HostRegistration;
            record.HostRegistration = default;
            cts = record.Cts;

            // 与 TerminalCommitted=true 同临界区摘取：此后新的 WaitAsync 直接读终态，不会挂到空列表上。
            detachWaiters = record.DetachWaiters.ToArray();
            record.DetachWaiters.Clear();
        }

        timer?.Dispose();

        try
        {
            hostRegistration.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // 宿主 token 已释放：无副作用。
        }

        // 先 Cancel 再 Dispose（R7）；两者都在锁外，避免回调阻塞临界区。
        SafeCancel(cts);

        try
        {
            cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // 已释放：终态语义已提交。
        }

        record.TerminalSignal.TrySetResult(snapshot);

        // 关闭剩余的 detach 等待者：它们不是被「转后台」放行的，改为拿终态
        // （detach 结果为 false 时 WaitAsync 会继续 await TerminalSignal）。
        foreach (var waiter in detachWaiters)
            waiter.TrySetResult(false);

        return true;
    }

    /// <summary>
    /// 终态提交＋落盘＋发事件；<b>返回时终态记录已落盘</b>。
    /// <para>CAS 失败（已终态）时不落盘、不发事件，直接返回。</para>
    /// </summary>
    private async Task FinalizeRecordAsync(
        BackgroundActivityRecord record,
        BackgroundActivityStatus status,
        BackgroundActivityCancelReason? cancelReason,
        BackgroundActivityResult? result,
        CancellationToken ct = default)
    {
        // ct 只表达调用方放弃等待：终态记录必须落盘，因此不传给存储（存储接口本身也无 ct）。
        if (!TryCommitTerminalState(record, status, cancelReason, result, out var snapshot, out var persistence))
            return;

        var persisted = await persistence.ConfigureAwait(false);

        if (!persisted)
        {
            _logger.LogError(
                "后台活动终态未能落盘，跳过完成通知 ActivityId={ActivityId} Status={Status}",
                snapshot.Id,
                snapshot.Status);
            return;
        }

        // 先落盘再投递（spec §19 红线 6）：只有终态记录确实写出后才允许写 outbox。
        await TryEnqueueCompletionNotificationAsync(snapshot).ConfigureAwait(false);
    }

    /// <summary>提交取消终态、落盘发事件，然后通知 launcher（先写状态再通知，spec §7.4）。</summary>
    private async Task<bool> CancelCoreAsync(
        BackgroundActivityRecord record,
        BackgroundActivityCancelReason reason,
        LauncherRegistration? launcherOverride = null)
    {
        var status = reason == BackgroundActivityCancelReason.HostShutdown
            ? BackgroundActivityStatus.Interrupted
            : BackgroundActivityStatus.Cancelled;

        if (!TryCommitTerminalState(record, status, reason, result: null, out var snapshot, out var persistence))
            return false;

        var persisted = await persistence.ConfigureAwait(false);

        if (!persisted)
        {
            _logger.LogError(
                "后台活动取消终态未能落盘，跳过完成通知 ActivityId={ActivityId} Status={Status}",
                snapshot.Id,
                snapshot.Status);
        }
        else
        {
            // 取消同样是终态：落盘之后再决定是否唤醒
            // （Interrupted / 非 Timeout 的 Cancelled / NotifyPolicy=None 由判定与策略自行跳过）。
            await TryEnqueueCompletionNotificationAsync(snapshot).ConfigureAwait(false);
        }

        await NotifyLauncherCancelledAsync(record, reason, launcherOverride).ConfigureAwait(false);
        return true;
    }

    private void OnTimeout(string activityId)
    {
        if (!_activities.TryGetValue(activityId, out var record))
            return;

        // 定时器回调是同步的：取消（含落盘、事件、launcher 通知）在后台串行完成。
        _ = CancelCoreAsync(record, BackgroundActivityCancelReason.Timeout);
    }

    private void OnHostStopping(BackgroundActivityRecord record)
    {
        _ = CancelCoreAsync(record, BackgroundActivityCancelReason.HostShutdown);
    }

    private async Task NotifyLauncherCancelledAsync(
        BackgroundActivityRecord record,
        BackgroundActivityCancelReason reason,
        LauncherRegistration? launcherOverride = null)
    {
        if (record.LauncherKind is null)
            return;

        // 注销路径传入已摘取的引用：此刻字典里可能已无该 kind（R3）。
        var registration = launcherOverride;
        if (registration is null && !_launchers.TryGetValue(record.LauncherKind, out registration))
            return;

        try
        {
            await registration.Launcher.CancelAsync(record.Handle, reason).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "通知 launcher 取消失败 ActivityId={ActivityId} Kind={Kind} Reason={Reason}",
                record.Handle.ActivityId,
                record.LauncherKind,
                reason);
        }
    }

    // ─── 执行观察 ─────────────────────────────────────────────────────────────

    private void ObserveExecution(BackgroundActivityRecord record, Task? execution)
    {
        if (execution is null)
        {
            _ = FinalizeRecordAsync(
                record,
                BackgroundActivityStatus.Failed,
                cancelReason: null,
                NoTerminalStateResult("launcher 未返回 Execution。"));
            return;
        }

        _ = ObserveExecutionAsync(record, execution);
    }

    private async Task ObserveExecutionAsync(BackgroundActivityRecord record, Task execution)
    {
        try
        {
            await execution.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 执行被取消（多为 manager 已提交 Cancelled）：只在尚无终态时兜底。
            await FinalizeRecordAsync(record, BackgroundActivityStatus.Cancelled, cancelReason: null, result: null)
                .ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "后台活动执行异常 ActivityId={ActivityId}", record.Handle.ActivityId);
            await FinalizeRecordAsync(
                    record,
                    BackgroundActivityStatus.Failed,
                    cancelReason: null,
                    new BackgroundActivityResult(
                        BackgroundActivityResultKind.None,
                        ArtifactPath: null,
                        ResultSessionId: null,
                        ExitCode: null,
                        "execution_faulted",
                        ex.Message))
                .ConfigureAwait(false);
            return;
        }

        // 正常结束但从未上报终态：不得当成成功（spec §7.4 / §8）。
        await FinalizeRecordAsync(
                record,
                BackgroundActivityStatus.Failed,
                cancelReason: null,
                NoTerminalStateResult(null))
            .ConfigureAwait(false);
    }

    private async Task ObserveCompletionAsync(BackgroundActivityRecord record, Task completion)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FinalizeRecordAsync(record, BackgroundActivityStatus.Cancelled, cancelReason: null, result: null)
                .ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "登记型后台活动完成信号异常 ActivityId={ActivityId}", record.Handle.ActivityId);
            await FinalizeRecordAsync(
                    record,
                    BackgroundActivityStatus.Failed,
                    cancelReason: null,
                    new BackgroundActivityResult(
                        BackgroundActivityResultKind.None,
                        ArtifactPath: null,
                        ResultSessionId: null,
                        ExitCode: null,
                        "completion_faulted",
                        ex.Message))
                .ConfigureAwait(false);
            return;
        }

        // 宿主只给了「完成」信号、没给结果：按无结果成功落终态（供 outbox 通知，不伪造结果内容）。
        await FinalizeRecordAsync(
                record,
                BackgroundActivityStatus.Succeeded,
                cancelReason: null,
                new BackgroundActivityResult(
                    BackgroundActivityResultKind.None,
                    ArtifactPath: null,
                    ResultSessionId: null,
                    ExitCode: null,
                    ErrorCode: null,
                    ErrorMessage: null))
            .ConfigureAwait(false);
    }

    // ─── 辅助 ─────────────────────────────────────────────────────────────────

    private BackgroundActivityRecord CreateRecord(
        BackgroundActivitySnapshot snapshot,
        string? launcherKind,
        string? dedupeKey,
        TimeSpan? timeout)
    {
        var cts = _hostStoppingToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(_hostStoppingToken)
            : new CancellationTokenSource();

        var reporter = new BackgroundActivityReporter(this, snapshot.Id);
        var handle = new BackgroundActivityHandle(snapshot.Id, reporter, cts.Token);
        var record = new BackgroundActivityRecord(snapshot, cts, handle, launcherKind, dedupeKey);

        if (timeout is { } effective && effective > TimeSpan.Zero)
        {
            record.TimeoutTimer = _timeProvider.CreateTimer(
                static state =>
                {
                    var (manager, activityId) = ((BackgroundActivityManager, string))state!;
                    try
                    {
                        manager.OnTimeout(activityId);
                    }
                    catch (Exception ex)
                    {
                        manager._logger.LogWarning(ex, "后台活动超时处理失败 ActivityId={ActivityId}", activityId);
                    }
                },
                (this, snapshot.Id),
                effective,
                Timeout.InfiniteTimeSpan);
        }

        if (_hostStoppingToken.CanBeCanceled)
        {
            var activityId = snapshot.Id;
            record.HostRegistration = _hostStoppingToken.Register(
                () =>
                {
                    if (_activities.TryGetValue(activityId, out var live))
                        OnHostStopping(live);
                });
        }

        return record;
    }

    private BackgroundActivityRecord? FindDedupeCandidate(BackgroundActivityStartRequest request)
    {
        lock (_sync)
        {
            foreach (var record in _activities.Values)
            {
                if (record.TerminalCommitted)
                    continue;

                if (!string.Equals(record.DedupeKey, request.DedupeKey, StringComparison.Ordinal))
                    continue;

                if (!string.Equals(record.Snapshot.Kind, request.Kind, StringComparison.Ordinal))
                    continue;

                if (!string.Equals(record.Snapshot.OwnerSessionId, request.OwnerSessionId, StringComparison.Ordinal))
                    continue;

                return record;
            }
        }

        return null;
    }

    private async Task<int> CancelMatchingAsync(
        Func<BackgroundActivitySnapshot, bool> predicate,
        BackgroundActivityCancelReason reason,
        CancellationToken ct)
    {
        var targets = new List<BackgroundActivityRecord>();
        lock (_sync)
        {
            foreach (var record in _activities.Values)
            {
                if (record.TerminalCommitted)
                    continue;

                if (predicate(record.Snapshot))
                    targets.Add(record);
            }
        }

        var cancelled = 0;
        foreach (var record in targets)
        {
            ct.ThrowIfCancellationRequested();
            if (await CancelCoreAsync(record, reason).ConfigureAwait(false))
                cancelled++;
        }

        return cancelled;
    }

    /// <summary>
    /// 统计某会话「占用后台配额」的活动数（<b>必须在 <c>_sync</c> 内调用</b>）。
    /// <para>
    /// 前台工具调用（<see cref="BackgroundActivityOrigin.ToolForeground"/>）不计入：
    /// 它的并发属于执行链的职责，不属于后台活动配额（否则普通 <c>bash</c> 会被自己挡死）。
    /// 后台 <see cref="BackgroundActivityOrigin.ToolBackground"/> / <see cref="BackgroundActivityOrigin.Scheduler"/> /
    /// <see cref="BackgroundActivityOrigin.Module"/> / <see cref="BackgroundActivityOrigin.User"/> 仍计入。
    /// </para>
    /// </summary>
    private int CountBackgroundActivitiesLocked(string ownerSessionId)
    {
        var count = 0;
        foreach (var candidate in _activities.Values)
        {
            if (candidate.TerminalCommitted)
                continue;

            if (candidate.Snapshot.Origin == BackgroundActivityOrigin.ToolForeground)
                continue;

            if (string.Equals(candidate.Snapshot.OwnerSessionId, ownerSessionId, StringComparison.Ordinal))
                count++;
        }

        return count;
    }

    private List<BackgroundActivityRecord> CollectSubtree(BackgroundActivityRecord root)
    {
        List<BackgroundActivityRecord> all;
        lock (_sync)
        {
            all = _activities.Values.ToList();
        }

        var children = new Dictionary<string, List<BackgroundActivityRecord>>(StringComparer.Ordinal);
        foreach (var record in all)
        {
            var parentId = record.Snapshot.ParentActivityId;
            if (string.IsNullOrEmpty(parentId))
                continue;

            if (!children.TryGetValue(parentId, out var siblings))
            {
                siblings = new List<BackgroundActivityRecord>();
                children[parentId] = siblings;
            }

            siblings.Add(record);
        }

        var result = new List<BackgroundActivityRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<BackgroundActivityRecord>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var id = current.Handle.ActivityId;
            if (!seen.Add(id))
                continue;

            result.Add(current);

            if (children.TryGetValue(id, out var descendants))
            {
                foreach (var descendant in descendants)
                    queue.Enqueue(descendant);
            }
        }

        return result;
    }

    /// <summary>
    /// 读侧判定：本会话活动，或 <c>Project</c> / <c>System</c> 可见性的活动（仅 <see cref="Get"/> 用）。
    /// </summary>
    private bool TryGetReadable(
        string activityId,
        string requesterSessionId,
        out BackgroundActivityRecord record)
        => TryGetCore(activityId, requesterSessionId, allowForeignVisible: true, out record);

    /// <summary>
    /// 写侧判定：<b>只</b>允许归属会话（取消 / 等待 / 读输出 / 标记已观察）。
    /// <para>可见性放宽只影响读，不能把"能看见"变成"能操作"。</para>
    /// </summary>
    private bool TryGetOwned(
        string activityId,
        string requesterSessionId,
        out BackgroundActivityRecord record)
        => TryGetCore(activityId, requesterSessionId, allowForeignVisible: false, out record);

    private bool TryGetCore(
        string activityId,
        string requesterSessionId,
        bool allowForeignVisible,
        out BackgroundActivityRecord record)
    {
        record = null!;
        if (string.IsNullOrEmpty(activityId) || string.IsNullOrEmpty(requesterSessionId))
            return false;

        if (!_activities.TryGetValue(activityId, out var candidate))
            return false;

        BackgroundActivitySnapshot snapshot;
        lock (_sync)
        {
            snapshot = candidate.Snapshot;
        }

        var allowed = string.Equals(snapshot.OwnerSessionId, requesterSessionId, StringComparison.Ordinal)
            || (allowForeignVisible && snapshot.Visibility != BackgroundActivityVisibility.Session);

        if (!allowed)
            return false;

        record = candidate;
        return true;
    }

    private BackgroundActivitySnapshot? CurrentSnapshot(BackgroundActivityRecord record)
    {
        lock (_sync)
        {
            return record.Snapshot;
        }
    }

    private bool IsTerminalCommitted(BackgroundActivityRecord record)
    {
        lock (_sync)
        {
            return record.TerminalCommitted;
        }
    }

    private Task<bool> EnqueuePersist(
        BackgroundActivityRecord record,
        BackgroundActivitySnapshot snapshot,
        string? eventType)
    {
        // 链式串行：落盘 → 发事件（保证「落盘先于事件」与状态写盘不乱序）。
        // 返回值是该环的完成信号+落盘结果：终态路径 await 它决定是否写 outbox，非终态路径丢弃即可。
        lock (_sync)
        {
            var chained = ChainPersistAsync(record.PersistChain, snapshot, eventType);
            record.PersistChain = chained;
            return chained;
        }
    }

    /// <summary>
    /// 落盘 → 发事件的一环。返回值是<b>该环是否落盘成功</b>：调用方（终态路径）据此决定是否写 outbox
    /// ——「先落盘再投递」不能只是顺序，落盘失败时也不得投递（spec §19 红线 6）。
    /// </summary>
    private async Task<bool> ChainPersistAsync(
        Task<bool> previous,
        BackgroundActivitySnapshot snapshot,
        string? eventType)
    {
        // 先让出，避免在 _sync 临界区内进入存储实现（链式赋值在同一把锁内完成）。
        await Task.Yield();

        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // 前一环已记录，链路继续。
        }

        var persisted = false;
        try
        {
            await _store.SaveAsync(snapshot).ConfigureAwait(false);
            persisted = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "后台活动快照落盘失败，跳过事件发布 ActivityId={ActivityId} Status={Status} Type={Type}",
                snapshot.Id,
                snapshot.Status,
                eventType);
        }

        // R6：只有落盘成功才发布事件——事件是「已持久事实」的实时投影，不能先于权威记录。
        if (persisted && eventType is not null)
            Publish(snapshot, eventType);

        return persisted;
    }

    private void Publish(BackgroundActivitySnapshot snapshot, string eventType, string? outputTail = null)
    {
        try
        {
            _eventPublisher.Publish(
                snapshot.OwnerSessionId,
                new BackgroundActivityEvent
                {
                    SessionId = snapshot.OwnerSessionId,
                    Type = eventType,
                    Activity = snapshot,
                    OutputTail = outputTail,
                    Timestamp = _timeProvider.GetLocalNow().DateTime
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "后台活动事件发布失败 ActivityId={ActivityId} Type={Type}",
                snapshot.Id,
                eventType);
        }
    }

    /// <summary>等待某活动所有已排队的落盘/事件完成（测试与停机路径用）。</summary>
    internal async Task WaitForPersistenceAsync(string activityId, CancellationToken ct = default)
    {
        Task<bool> chain;
        lock (_sync)
        {
            if (!_activities.TryGetValue(activityId, out var record))
                return;

            chain = record.PersistChain;
        }

        await chain.WaitAsync(ct).ConfigureAwait(false);
    }

    // ─── 静态辅助 ─────────────────────────────────────────────────────────────

    private static string NewActivityId() => ActivityIdPrefix + Guid.NewGuid().ToString("N");

    private static bool IsTerminal(BackgroundActivityStatus status) => status
        is BackgroundActivityStatus.Succeeded
        or BackgroundActivityStatus.Failed
        or BackgroundActivityStatus.Cancelled
        or BackgroundActivityStatus.Interrupted;

    private static string ResolveEventType(BackgroundActivityStatus status) => status switch
    {
        BackgroundActivityStatus.Succeeded => ActivityEventTypes.Completed,
        BackgroundActivityStatus.Failed => ActivityEventTypes.Failed,
        BackgroundActivityStatus.Cancelled => ActivityEventTypes.Cancelled,
        BackgroundActivityStatus.Interrupted => ActivityEventTypes.Cancelled,
        _ => ActivityEventTypes.Progress
    };

    private static BackgroundActivityResult NoTerminalStateResult(string? message) => new(
        BackgroundActivityResultKind.None,
        ArtifactPath: null,
        ResultSessionId: null,
        ExitCode: null,
        "no_terminal_state",
        message ?? "执行已结束但未上报终态。");

    private static IReadOnlyDictionary<string, string> CopyAttributes(IReadOnlyDictionary<string, string>? attributes)
        => attributes is null || attributes.Count == 0
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(attributes);

    private static void SafeCancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放：取消语义已由终态提交表达。
        }
        catch (AggregateException)
        {
            // 个别回调抛异常不影响取消语义。
        }
    }

    /// <summary>准入失败（并发上限 / kind 注销中）时释放尚未入字典的 record，避免定时器与登记泄漏（R7）。</summary>
    private static void ReleaseRejectedRecord(BackgroundActivityRecord record)
    {
        record.TimeoutTimer?.Dispose();
        record.TimeoutTimer = null;

        try
        {
            record.HostRegistration.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // 宿主 token 已释放：无副作用。
        }
        finally
        {
            record.HostRegistration = default;
        }

        try
        {
            record.Cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // 已释放。
        }
    }

    private TimeSpan ResolveTimeout(TimeSpan? requested, BackgroundActivityOptions options)
    {
        var value = requested ?? options.DefaultTimeout;
        if (value <= TimeSpan.Zero)
            value = options.DefaultTimeout;

        return value > options.MaxTimeout ? options.MaxTimeout : value;
    }

    private string ResolveWorkspaceRoot()
        => _services.GetService<IWorkspaceProvider>()?.ResolvedRoot ?? string.Empty;

    /// <summary>
    /// 终态落盘成功后写 outbox（spec §10.1 第 2 步）。
    /// <para>
    /// 条件全部来自<b>契约字段</b>：<see cref="BackgroundActivityNotifyPolicy.OnComplete"/> +
    /// <see cref="BackgroundActivityCapabilities.SignalsCompletion"/> + 结果未被观察。
    /// <c>Interrupted</c>（宿主关闭/孤儿收敛）一律不投递。
    /// </para>
    /// <para>
    /// <b>只有「任务结束且值得让会话知道」的终态才入队</b>（判定见
    /// <see cref="BackgroundActivityNotificationPolicy.IsCompletionNotifiable"/>）：
    /// <see cref="BackgroundActivityStatus.Succeeded"/>、<see cref="BackgroundActivityStatus.Failed"/>，
    /// 以及 <see cref="BackgroundActivityStatus.Cancelled"/> + <see cref="BackgroundActivityCancelReason.Timeout"/>
    /// （超时是失败，不能被静默）。
    /// </para>
    /// <para>
    /// 其余 <see cref="BackgroundActivityStatus.Cancelled"/>（用户取消、会话 Stop、工作区切换、
    /// 模块注销、被取代、父级级联）与 <see cref="BackgroundActivityStatus.Interrupted"/> 一律不入队：
    /// 它们本来要求「不再产出」，若在合并窗口后唤醒会话再跑一轮，「停止后不再产出」随即失效；
    /// 用户显式 <c>activity_cancel</c> 后也不该被唤醒。
    /// </para>
    /// <para>
    /// 写 outbox 失败不得反向破坏终态：只记日志，快照已经是权威事实。
    /// </para>
    /// </summary>
    private async Task TryEnqueueCompletionNotificationAsync(BackgroundActivitySnapshot snapshot)
    {
        // 取消类终态不产生「完成」唤醒；超时例外（超时是失败）。
        if (!BackgroundActivityNotificationPolicy.IsCompletionNotifiable(snapshot))
            return;

        if (snapshot.NotifyPolicy != BackgroundActivityNotifyPolicy.OnComplete)
            return;

        if ((snapshot.Capabilities & BackgroundActivityCapabilities.SignalsCompletion) == 0)
            return;

        if (snapshot.ResultObservedAt is not null)
            return;

        try
        {
            await _notifications.EnqueueAsync(new BackgroundActivityNotification
            {
                // 批次身份由 outbox 分配：这里只声明「哪个会话的哪个活动完成了」。
                DigestId = string.Empty,
                HostInstanceId = snapshot.HostInstanceId,
                OwnerSessionId = snapshot.OwnerSessionId,
                ActivityIds = new[] { snapshot.Id }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "后台活动完成通知入队失败 ActivityId={ActivityId} SessionId={SessionId}",
                snapshot.Id,
                snapshot.OwnerSessionId);
        }
    }

    private sealed record LauncherRegistration(string ModuleId, IBackgroundActivityLauncher Launcher);
}

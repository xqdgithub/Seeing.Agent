using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Questions;
using Seeing.Session.Core;

namespace Seeing.Agent.Tui.Services;

/// <summary>
/// TUI 可呈现性提供方：维护「活跃会话 ∪ 活跃会话组全部成员（递归子会话）∪ 在途 Permission/Question 所属会话」的稳定快照。
/// <para>
/// <see cref="SurfaceSessionIds"/> 返回缓存快照，不实时读取易变状态；变更经 50ms 去抖发布
/// <see cref="SurfacedChanged"/>，空集候选延迟 1s 确认提交（期间恢复非空则取消）。
/// </para>
/// <para>注册/注销由引擎负责（权限与问答是两个独立的 <see cref="ISurfaceRegistry"/> 实例，各注册一次）。</para>
/// </summary>
public sealed class TuiSurfaceProvider : ISurfaceProvider, IDisposable
{
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan DefaultEmptyConfirm = TimeSpan.FromSeconds(1);

    private readonly ISessionGroupManager _groupManager;
    private readonly ISessionGroupEventBus _groupBus;
    private readonly IPermissionRequestManager _permissionManager;
    private readonly IQuestionRequestManager _questionManager;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _emptyConfirm;
    private readonly ILogger<TuiSurfaceProvider>? _logger;
    private readonly object _gate = new();
    private readonly HashSet<string> _confirmed = new(StringComparer.Ordinal);

    private HashSet<string> _groupMemberIds = new(StringComparer.Ordinal);
    private string? _activeSessionId;
    private string? _activeGroupId;
    private string? _subscribedGroupId;
    private CancellationTokenSource? _groupSubscription;
    private Task? _groupConsumeTask;
    private Timer? _debounceTimer;
    private Timer? _emptyTimer;
    private bool _initialized;
    private bool _disposed;

    /// <summary>
    /// 构造：默认 50ms 去抖、1s 空集确认；时间窗口与日志为可选（供测试注入）。
    /// </summary>
    public TuiSurfaceProvider(
        ISessionGroupManager groupManager,
        ISessionGroupEventBus groupBus,
        IPermissionRequestManager permissionManager,
        IQuestionRequestManager questionManager,
        TimeSpan? debounce = null,
        TimeSpan? emptyConfirm = null,
        ILogger<TuiSurfaceProvider>? logger = null)
    {
        _groupManager = groupManager ?? throw new ArgumentNullException(nameof(groupManager));
        _groupBus = groupBus ?? throw new ArgumentNullException(nameof(groupBus));
        _permissionManager = permissionManager ?? throw new ArgumentNullException(nameof(permissionManager));
        _questionManager = questionManager ?? throw new ArgumentNullException(nameof(questionManager));
        _debounce = debounce ?? DefaultDebounce;
        _emptyConfirm = emptyConfirm ?? DefaultEmptyConfirm;
        _logger = logger;

        _debounceTimer = new Timer(
            _ => OnDebounceElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _emptyTimer = new Timer(
            _ => OnEmptyConfirmElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> SurfaceSessionIds
    {
        get
        {
            lock (_gate)
                return _confirmed.ToArray();
        }
    }

    /// <inheritdoc />
    public event Action? SurfacedChanged;

    /// <summary>订阅在途请求变更与活跃组事件（幂等）。</summary>
    public void Initialize()
    {
        lock (_gate)
        {
            if (_disposed || _initialized)
                return;
            _initialized = true;
            _permissionManager.PendingChanged += OnPendingChanged;
            _questionManager.PendingChanged += OnPendingChanged;
        }

        ResubscribeActiveGroup();
    }

    /// <summary>切换活跃会话并重算组快照（含递归子会话）。</summary>
    public async Task SetActiveSessionAsync(string sessionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var snapshot = await ResolveGroupSnapshotAsync(sessionId, ct).ConfigureAwait(false);

        lock (_gate)
        {
            if (_disposed)
                return;
            _activeSessionId = sessionId;
            _groupMemberIds = snapshot.Members;
            _activeGroupId = snapshot.GroupId;
        }

        ResubscribeActiveGroup();
        RequestRefresh();
    }

    private async Task<GroupSnapshot> ResolveGroupSnapshotAsync(string? sessionId, CancellationToken ct)
    {
        var members = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(sessionId))
            return new GroupSnapshot(members, null);

        members.Add(sessionId);
        string? groupId = null;

        var group = await _groupManager.GetGroupForSessionAsync(sessionId, ct).ConfigureAwait(false);
        if (group is not null)
        {
            groupId = group.Id;
            foreach (var member in group.Members)
            {
                if (!string.IsNullOrEmpty(member.SessionId))
                    members.Add(member.SessionId);
            }
        }

        // 递归子会话（含冷兜底场景下未进组快照的 Child 子树）
        var visited = new HashSet<string>(StringComparer.Ordinal) { sessionId };
        var queue = new Queue<string>();
        queue.Enqueue(sessionId);
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var parent = queue.Dequeue();
            var children = await _groupManager.ListChildrenAsync(parent, ct).ConfigureAwait(false);
            if (children is null)
                continue;
            foreach (var child in children)
            {
                if (child is null || string.IsNullOrEmpty(child.Id))
                    continue;
                members.Add(child.Id);
                if (visited.Add(child.Id))
                    queue.Enqueue(child.Id);
            }
        }

        return new GroupSnapshot(members, groupId);
    }

    private void OnPendingChanged() => RequestRefresh();

    private void ResubscribeActiveGroup()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            var groupId = _activeGroupId;
            if (string.Equals(_subscribedGroupId, groupId, StringComparison.Ordinal))
                return;

            _subscribedGroupId = groupId;

            var previous = _groupSubscription;
            _groupSubscription = null;
            _groupConsumeTask = null;
            if (previous is not null)
            {
                try
                {
                    previous.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                previous.Dispose();
            }

            if (string.IsNullOrEmpty(groupId))
                return;

            var cts = new CancellationTokenSource();
            _groupSubscription = cts;
            // 记录消费任务句柄，供 Dispose 有界等待，避免 fire-and-forget 泄漏。
            _groupConsumeTask = Task.Run(() => ConsumeGroupEventsAsync(groupId, cts.Token), CancellationToken.None);
        }
    }

    private async Task ConsumeGroupEventsAsync(string groupId, CancellationToken ct)
    {
        try
        {
            var stream = _groupBus.SubscribeAsync(groupId, ct);
            if (stream is null)
                return;

            await foreach (var evt in stream.WithCancellation(ct).ConfigureAwait(false))
            {
                if (evt is null)
                    continue;
                ApplyGroupSnapshot(evt);
                RequestRefresh();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "会话组事件订阅失败: {GroupId}", groupId);
        }
    }

    private void ApplyGroupSnapshot(SessionGroupChangedEvent evt)
    {
        var members = new HashSet<string>(StringComparer.Ordinal);
        lock (_gate)
        {
            if (_disposed)
                return;
            if (!string.IsNullOrEmpty(_activeSessionId))
                members.Add(_activeSessionId);
            foreach (var member in evt.Members)
            {
                if (!string.IsNullOrEmpty(member.SessionId))
                    members.Add(member.SessionId);
            }
            _groupMemberIds = members;
        }
    }

    private void RequestRefresh()
    {
        if (_disposed)
            return;

        bool empty;
        lock (_gate)
            empty = _confirmed.Count == 0;

        // 稳定快照仍为空时立即收敛，避免首帧窗口内因 CanSurface=false 被 fail-closed。
        if (empty)
        {
            OnDebounceElapsed();
            return;
        }

        _debounceTimer?.Change(_debounce, Timeout.InfiniteTimeSpan);
    }

    private void OnDebounceElapsed()
    {
        if (_disposed)
            return;

        var candidate = ComputeSurface();
        bool report;
        lock (_gate)
        {
            if (candidate.Count > 0)
            {
                CancelEmptyConfirmLocked();
                report = !_confirmed.SetEquals(candidate);
                _confirmed.Clear();
                _confirmed.UnionWith(candidate);
            }
            else
            {
                report = false;
                StartEmptyConfirmLocked();
            }
        }

        if (report)
            NotifySurfacedChanged();
    }

    private void OnEmptyConfirmElapsed()
    {
        if (_disposed)
            return;

        var candidate = ComputeSurface();
        bool report;
        lock (_gate)
        {
            if (candidate.Count > 0)
            {
                report = !_confirmed.SetEquals(candidate);
                _confirmed.Clear();
                _confirmed.UnionWith(candidate);
            }
            else
            {
                report = _confirmed.Count > 0;
                _confirmed.Clear();
            }
        }

        if (report)
            NotifySurfacedChanged();
    }

    private HashSet<string> ComputeSurface()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        lock (_gate)
        {
            if (!string.IsNullOrEmpty(_activeSessionId))
                result.Add(_activeSessionId);
            result.UnionWith(_groupMemberIds);
        }

        try
        {
            var permissions = _permissionManager.GetAllPending();
            if (permissions is not null)
            {
                foreach (var request in permissions)
                {
                    if (request is not null && !string.IsNullOrEmpty(request.SessionId))
                        result.Add(request.SessionId);
                }
            }

            var questions = _questionManager.GetAllPending();
            if (questions is not null)
            {
                foreach (var request in questions)
                {
                    if (request is not null && !string.IsNullOrEmpty(request.SessionId))
                        result.Add(request.SessionId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "计算可呈现快照时读取在途请求失败");
        }

        return result;
    }

    private void StartEmptyConfirmLocked()
        => _emptyTimer?.Change(_emptyConfirm, Timeout.InfiniteTimeSpan);

    private void CancelEmptyConfirmLocked()
        => _emptyTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    private void NotifySurfacedChanged()
    {
        try
        {
            SurfacedChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "可呈现性变更通知失败");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        CancellationTokenSource? subscription;
        Task? consumeTask;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            subscription = _groupSubscription;
            _groupSubscription = null;
            consumeTask = _groupConsumeTask;
            _groupConsumeTask = null;
        }

        _permissionManager.PendingChanged -= OnPendingChanged;
        _questionManager.PendingChanged -= OnPendingChanged;

        // 顺序：先取消 → 有界等待消费任务收敛 → 释放订阅，避免后台任务继续触碰已释放字段。
        if (subscription is not null)
        {
            try
            {
                subscription.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (consumeTask is not null)
        {
            try
            {
                consumeTask.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        subscription?.Dispose();

        _debounceTimer?.Dispose();
        _debounceTimer = null;
        _emptyTimer?.Dispose();
        _emptyTimer = null;

        SurfacedChanged = null;

        lock (_gate)
        {
            _confirmed.Clear();
            _groupMemberIds.Clear();
        }
    }

    private readonly record struct GroupSnapshot(HashSet<string> Members, string? GroupId);
}

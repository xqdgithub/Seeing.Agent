using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Ui.Workbench;

namespace Seeing.Agent.WebUI.Workbench;

/// <summary>
/// 工作台快照上报桥（Scoped，circuit 维度）：把本地 dock 布局投影成服务端可见快照并去抖推送。
/// <para>
/// <b>为什么去抖</b>：拖动宽度会高频触发 <see cref="WorkbenchDockState.Changed"/>；
/// 每次推送都让上下文源重建会让提示词基线反复抖动。250ms 后稳定值才上报。
/// </para>
/// <para><b>只上报稳定事实</b>：dock、工作台 id、标题、是否激活、上下文摘要。不推送 UI 内部结构。</para>
/// </summary>
public sealed class WorkbenchHostBridge : IDisposable
{
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(250);

    private readonly WorkbenchDockState _state;
    private readonly IWorkbenchSessionStateStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _debounce;
    private readonly ILogger<WorkbenchHostBridge>? _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, string?> _summaries = new(StringComparer.Ordinal);

    private ITimer? _timer;
    private string? _pendingSessionId;
    private bool _disposed;

    /// <summary>创建桥。</summary>
    public WorkbenchHostBridge(
        WorkbenchDockState state,
        IWorkbenchSessionStateStore store,
        TimeProvider? timeProvider = null,
        TimeSpan? debounce = null,
        ILogger<WorkbenchHostBridge>? logger = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _debounce = debounce ?? DefaultDebounce;
        _logger = logger;

        _state.Changed += OnStateChanged;
    }

    /// <summary>记录某实例的上下文摘要（由工作台上报）。</summary>
    public void SetSummary(string sessionId, WorkbenchInstanceKey key, string? summary)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        lock (_gate)
            _summaries[SummaryKey(sessionId, key)] = Normalize(summary);
    }

    /// <summary>立即推送某会话的快照（跳过去抖；切换会话 / 页面卸载时用）。</summary>
    public void Flush(string sessionId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(sessionId))
            return;

        var snapshots = BuildSnapshots(sessionId, descriptors: null);
        _store.Update(sessionId, snapshots);
    }

    /// <summary>
    /// 立即推送某会话的快照，并用当前声明补齐标题（模块停用后实例已被 <c>Prune</c> 移除，
    /// 因此这里只处理仍存在的实例）。
    /// </summary>
    public void Flush(string sessionId, IReadOnlyList<WorkbenchContribution> descriptors)
    {
        if (_disposed || string.IsNullOrWhiteSpace(sessionId))
            return;

        _store.Update(sessionId, BuildSnapshots(sessionId, descriptors));
    }

    /// <summary>清空某会话快照（会话删除 / 组件卸载）。</summary>
    public void Clear(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        // 摘要按实例键分桶，但没有会话维度 —— 切换会话时由 dock 宿主的 Prune 负责收敛成员，
        // 这里只清服务端快照（实例级摘要随实例关闭自然不再被读取）。
        _store.Clear(sessionId);
    }

    private void OnStateChanged(string sessionId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(sessionId))
            return;

        lock (_gate)
        {
            _pendingSessionId = sessionId;
            _timer ??= _timeProvider.CreateTimer(_ => OnTimerElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimerElapsed()
    {
        string? sessionId;
        lock (_gate)
        {
            sessionId = _pendingSessionId;
            _pendingSessionId = null;
        }

        if (_disposed || string.IsNullOrWhiteSpace(sessionId))
            return;

        try
        {
            _store.Update(sessionId, BuildSnapshots(sessionId, descriptors: null));
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "工作台快照上报失败（忽略）：{SessionId}", sessionId);
        }
    }

    private IReadOnlyList<WorkbenchSessionSnapshot> BuildSnapshots(
        string sessionId,
        IReadOnlyList<WorkbenchContribution>? descriptors)
    {
        var layout = _state.GetLayout(sessionId);
        var contributesContext = descriptors is null
            ? null
            : new HashSet<string>(
                descriptors.Where(d => d.ContributesContext).Select(d => d.Id),
                StringComparer.Ordinal);

        var snapshots = new List<WorkbenchSessionSnapshot>();

        foreach (var instance in layout.Left.Concat(layout.Right))
        {
            string? summary;
            lock (_gate)
                _summaries.TryGetValue(SummaryKey(sessionId, instance.Key), out summary);

            var supportsContext = contributesContext is null
                ? summary is not null
                : contributesContext.Contains(instance.Key.WorkbenchId);

            snapshots.Add(new WorkbenchSessionSnapshot(
                instance.Key,
                instance.Title,
                instance.IsActive,
                supportsContext ? summary : null));
        }

        // 稳定顺序：dock → id → ordinal（下游上下文源的 diff 依赖它）
        return snapshots
            .OrderBy(snapshot => snapshot.Key.Dock)
            .ThenBy(snapshot => snapshot.Key.WorkbenchId, StringComparer.Ordinal)
            .ThenBy(snapshot => snapshot.Key.Ordinal)
            .ToArray();
    }

    private static string SummaryKey(string sessionId, WorkbenchInstanceKey key)
        => $"{sessionId}|{key}";

    private static string? Normalize(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return null;

        var flat = summary.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= WorkbenchContextLimits.SummaryCharLimit
            ? flat
            : flat[..WorkbenchContextLimits.SummaryCharLimit] + "…";
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _state.Changed -= OnStateChanged;

        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}

/// <summary>工作台摘要的长度约束（UI 与上下文源共用同一常量，避免两处漂移）。</summary>
public static class WorkbenchContextLimits
{
    /// <summary>单条上下文摘要最大字符数。</summary>
    public const int SummaryCharLimit = 200;

    /// <summary>单实例可恢复状态序列化后的字节上限。</summary>
    public const int ViewStateByteLimit = 2_048;
}

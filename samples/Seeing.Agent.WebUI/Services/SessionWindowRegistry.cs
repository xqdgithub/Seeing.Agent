using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Events;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 组驱动的通用会话窗口注册表（circuit 维度 IStreamConsumer，经
/// <see cref="SessionEventStreamRouter.GetOrCreateCircuitConsumer{T}"/> 获取/登记）。
/// <para>
/// 订阅会话组事件总线（<see cref="ISessionGroupEventBus"/>），维护组内成员窗口集合与 active/锚点。
/// 只维护窗口集合（SessionId/Relation/Kind/Title/IsActive），<b>不</b>持权威执行态：
/// IsExecuting 恒为 false，执行态由各窗口自身 handler 提供。
/// </para>
/// <para>
/// 不消费执行流事件（OnEvent/OnStreamEnd 为 no-op）；组变更经总线推送。
/// </para>
/// </summary>
public sealed class SessionWindowRegistry : IStreamConsumer, IDisposable
{
    /// <summary>
    /// 窗口节点：由组成员快照投影。
    /// <c>IsExecuting</c> 恒为 false（registry 不持权威执行态，由窗口自身 handler 渲染）。
    /// </summary>
    public sealed record WindowNode(
        string SessionId,
        SessionRelation Relation,
        SessionKind Kind,
        string Title,
        bool IsActive,
        bool IsExecuting);

    private readonly ISessionGroupManager _groupManager;
    private readonly ISessionGroupEventBus _groupEventBus;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<SessionWindowRegistry>? _logger;

    private readonly object _gate = new();
    private IReadOnlyList<WindowNode> _windows = Array.Empty<WindowNode>();
    private string _groupId = string.Empty;
    private string _anchorSessionId = string.Empty;
    private string _activeSessionId = string.Empty;
    private long _appliedVersion;
    private CancellationTokenSource? _cts;
    private int _rebindGeneration;
    private bool _disposed;

    public SessionWindowRegistry(
        ISessionGroupManager groupManager,
        ISessionGroupEventBus groupEventBus,
        ISessionManager sessionManager,
        ILogger<SessionWindowRegistry>? logger = null)
    {
        _groupManager = groupManager ?? throw new ArgumentNullException(nameof(groupManager));
        _groupEventBus = groupEventBus ?? throw new ArgumentNullException(nameof(groupEventBus));
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _logger = logger;
    }

    /// <summary>IStreamConsumer 标识：以锚点会话为标识（组驱动，不用执行流）。</summary>
    public string SessionId => AnchorSessionId;

    /// <summary>当前绑定的组 ID（未绑定时为空串）。</summary>
    public string GroupId { get { lock (_gate) return _groupId; } }

    /// <summary>组锚点会话 ID。</summary>
    public string AnchorSessionId { get { lock (_gate) return _anchorSessionId; } }

    /// <summary>组活跃会话 ID（组 <c>ResolveActiveId()</c> 语义，回退锚点）。</summary>
    public string ActiveSessionId { get { lock (_gate) return _activeSessionId; } }

    /// <summary>组内窗口快照（按成员 Order 排序）。</summary>
    public IReadOnlyList<WindowNode> Windows { get { lock (_gate) return _windows; } }

    /// <summary>窗口集合或 active 变化后触发（初始枚举/组事件/Rebind）。</summary>
    public event Action? WindowsChanged;

    /// <summary>
    /// 绑定会话：先订阅其所属组事件流，再枚举成员（避免枚举与订阅之间漏事件）。
    /// 同 circuit 换主会话路由时调用；立即清空旧窗口并触发一次通知。
    /// </summary>
    public void Rebind(string sessionId)
    {
        if (_disposed)
            return;

        var generation = Interlocked.Increment(ref _rebindGeneration);

        lock (_gate)
        {
            _windows = Array.Empty<WindowNode>();
            _groupId = string.Empty;
            _anchorSessionId = sessionId ?? string.Empty;
            _activeSessionId = sessionId ?? string.Empty;
            _appliedVersion = 0;
        }

        CancelSubscription();
        WindowsChanged?.Invoke(); // 换会话后即使枚举无新增也须清空旧窗口

        _ = RebindAsync(sessionId, generation);
    }

    private async Task RebindAsync(string? sessionId, int generation)
    {
        try
        {
            if (string.IsNullOrEmpty(sessionId))
                return;

            var groupRef = await _groupManager.GetGroupForSessionAsync(sessionId).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _rebindGeneration))
                return;

            if (groupRef == null)
            {
                _logger?.LogWarning("[SessionWindowRegistry] 未找到会话组，退化为单窗口: {SessionId}", sessionId);
                ApplyFallback(sessionId);
                return;
            }

            var groupId = groupRef.Id;

            // 先订阅（SubscribeAsync 同步完成注册）再枚举成员，避免两者之间漏事件
            var cts = new CancellationTokenSource();
            var old = Interlocked.Exchange(ref _cts, cts);
            old?.Cancel();
            old?.Dispose();

            var stream = _groupEventBus.SubscribeAsync(groupId, cts.Token);
            _ = ReadGroupLoopAsync(stream, cts.Token);

            var snapshot = await _groupManager.GetGroupAsync(groupId).ConfigureAwait(false) ?? groupRef;
            if (generation != Volatile.Read(ref _rebindGeneration))
                return;
            ApplyGroup(snapshot);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[SessionWindowRegistry] 绑定会话组失败: {SessionId}", sessionId);
        }
    }

    /// <summary>无组兜底：以当前会话构造单成员窗口（Relation=None）。</summary>
    private void ApplyFallback(string sessionId)
    {
        var session = _sessionManager.Get(sessionId);
        var node = new WindowNode(
            sessionId,
            SessionRelation.None,
            session?.Kind ?? SessionKind.Root,
            session?.Title ?? string.Empty,
            IsActive: true,
            IsExecuting: false);

        lock (_gate)
        {
            _windows = new[] { node };
            _anchorSessionId = sessionId;
            _activeSessionId = sessionId;
        }
        WindowsChanged?.Invoke();
    }

    /// <summary>应用组快照（枚举结果）：Version 小于已应用版本则丢弃。</summary>
    private void ApplyGroup(SessionGroup group)
    {
        lock (_gate)
        {
            if (group.Version < _appliedVersion)
            {
                _logger?.LogInformation(
                    "[SessionWindowRegistry] 丢弃过期组快照: GroupId={GroupId}, Version={Version} < {Applied}",
                    group.Id, group.Version, _appliedVersion);
                return;
            }

            _appliedVersion = group.Version;
            _groupId = group.Id;
            _anchorSessionId = group.AnchorSessionId;
            _activeSessionId = ResolveActiveId(group.ActiveSessionId, group.Members, group.AnchorSessionId);
            _windows = BuildWindows(group.Members, _activeSessionId);
        }
        WindowsChanged?.Invoke();
    }

    /// <summary>应用组事件快照：Version 小于已应用版本则丢弃。</summary>
    private void OnGroupEvent(SessionGroupChangedEvent evt)
    {
        lock (_gate)
        {
            if (evt.Version < _appliedVersion)
            {
                _logger?.LogInformation(
                    "[SessionWindowRegistry] 丢弃乱序组事件: GroupId={GroupId}, Version={Version} < {Applied}",
                    evt.GroupId, evt.Version, _appliedVersion);
                return;
            }

            _appliedVersion = evt.Version;
            _groupId = evt.GroupId;
            _anchorSessionId = evt.AnchorSessionId;
            _activeSessionId = ResolveActiveId(evt.ActiveSessionId, evt.Members, evt.AnchorSessionId);
            _windows = BuildWindows(evt.Members, _activeSessionId);
        }
        WindowsChanged?.Invoke();
    }

    /// <summary>活跃会话解析：仅当 active 是当前成员时采用，否则回退锚点。</summary>
    private static string ResolveActiveId(
        string? activeId, IReadOnlyList<SessionGroupMember> members, string anchorId)
    {
        if (!string.IsNullOrEmpty(activeId)
            && members.Any(m => string.Equals(m.SessionId, activeId, StringComparison.Ordinal)))
            return activeId!;

        return anchorId;
    }

    /// <summary>组成员 → 窗口节点投影（Child 关系为 SubAgent，其余为 Root）。</summary>
    private IReadOnlyList<WindowNode> BuildWindows(
        IReadOnlyList<SessionGroupMember> members, string activeId)
    {
        var nodes = new List<WindowNode>(members.Count);
        foreach (var member in members.OrderBy(m => m.Order))
        {
            var session = _sessionManager.Get(member.SessionId);
            nodes.Add(new WindowNode(
                member.SessionId,
                member.Relation,
                member.Relation == SessionRelation.Child ? SessionKind.SubAgent : SessionKind.Root,
                session?.Title ?? member.Label ?? string.Empty,
                string.Equals(member.SessionId, activeId, StringComparison.Ordinal),
                IsExecuting: false));
        }
        return nodes;
    }

    /// <summary>后台读取组事件流；取消/异常时静默退出（消费者异常隔离）。</summary>
    private async Task ReadGroupLoopAsync(
        IAsyncEnumerable<SessionGroupChangedEvent> stream, CancellationToken ct)
    {
        try
        {
            await foreach (var evt in stream.WithCancellation(ct).ConfigureAwait(false))
                OnGroupEvent(evt);
        }
        catch (OperationCanceledException)
        {
            // 正常取消：Rebind / Dispose
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[SessionWindowRegistry] 组事件循环异常终止");
        }
    }

    private void CancelSubscription()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts == null)
            return;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放，忽略
        }
        cts.Dispose();
    }

    // 组驱动：不消费执行流事件（接口兼容 IStreamConsumer 生命周期）。
    public void OnEvent(IMessageEvent evt) { }

    public void OnStreamEnd() { }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Interlocked.Increment(ref _rebindGeneration);
        CancelSubscription();
    }
}

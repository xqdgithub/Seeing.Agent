using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.WebUI.Models;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 权限卡片聚合器（Scoped，UI 投影聚合，非权威）。
/// <para>
/// 作为会话事件流消费者，将 <see cref="PermissionRequestEvent"/>/<see cref="PermissionResolvedEvent"/>
/// 投影为 <see cref="PermissionCardModel"/>（按 RequestId 聚合、按 CallId 关联工具卡片）；
/// 在途真相源始终是 <see cref="IPermissionRequestManager"/>——请求事件仅在 manager
/// 仍视为在途时建立卡片，会话加载时以 <c>GetPending</c> 重建（刷新重建）。
/// </para>
/// <para>
/// 缓冲重放对账：新订阅者会收到缓冲重放，已决议请求的陈旧请求事件不得复活为 pending
/// （先查已决议卡片，再与 manager.GetPending 对账）。
/// </para>
/// <para>并发写契约：所有变更在 <see cref="_gate"/> 内串行；<see cref="Changed"/> 在锁外触发。</para>
/// </summary>
public sealed class PermissionCardAggregator : IStreamConsumer, IDisposable
{
    private static readonly IReadOnlyList<PermissionGrantScope> DefaultScopes =
        new[] { PermissionGrantScope.Once, PermissionGrantScope.Session };

    private readonly IPermissionRequestManager _manager;
    private readonly ILogger<PermissionCardAggregator>? _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, PermissionCardModel> _cards = new(StringComparer.Ordinal);
    private string? _sessionId;
    private bool _disposed;

    public PermissionCardAggregator(
        IPermissionRequestManager manager,
        ILogger<PermissionCardAggregator>? logger = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _logger = logger;
    }

    /// <inheritdoc />
    public string SessionId => _sessionId ?? string.Empty;

    /// <summary>投影变更通知（Blazor 据此重渲染；锁外触发）。</summary>
    public event Action? Changed;

    /// <summary>当前全部卡片快照（含已决议项，按创建时间升序）。</summary>
    public IReadOnlyList<PermissionCardModel> Snapshot
    {
        get
        {
            lock (_gate)
                return _cards.Values.OrderBy(c => c.CreatedAt).ToList();
        }
    }

    /// <summary>当前会话的在途卡片快照。</summary>
    public IReadOnlyList<PermissionCardModel> PendingSnapshot
    {
        get
        {
            lock (_gate)
                return _cards.Values.Where(c => c.IsPending).OrderBy(c => c.CreatedAt).ToList();
        }
    }

    public PermissionCardModel? GetByRequestId(string? requestId)
    {
        if (string.IsNullOrEmpty(requestId))
            return null;

        lock (_gate)
            return _cards.TryGetValue(requestId, out var card) ? card : null;
    }

    /// <summary>按工具调用 ID 归纳（同一 CallId 可因能力门/资源门各生成一张卡片，故返回列表）。</summary>
    public IReadOnlyList<PermissionCardModel> GetByCallId(string? callId)
    {
        if (string.IsNullOrEmpty(callId))
            return Array.Empty<PermissionCardModel>();

        lock (_gate)
            return _cards.Values
                .Where(c => string.Equals(c.CallId, callId, StringComparison.Ordinal))
                .OrderBy(c => c.CreatedAt)
                .ToList();
    }

    /// <summary>
    /// 绑定会话（幂等）：会话变化时清空旧卡片并以 <c>GetPending</c> 重建（刷新重建）。
    /// </summary>
    public void Bind(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        lock (_gate)
        {
            if (string.Equals(_sessionId, sessionId, StringComparison.Ordinal))
                return;

            _sessionId = sessionId;
            _cards.Clear();
        }

        LoadPending(sessionId);
        NotifyChanged();
    }

    /// <summary>以 <c>GetPending</c> 刷新在途卡片（不清理已决议展示项）。</summary>
    public void Reconcile(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        lock (_gate)
            _sessionId = sessionId;

        LoadPending(sessionId);
        NotifyChanged();
    }

    /// <inheritdoc />
    public void OnEvent(IMessageEvent evt)
    {
        if (_disposed || evt is null)
            return;

        switch (evt)
        {
            case PermissionRequestEvent request:
                ApplyRequest(request);
                break;
            case PermissionResolvedEvent resolved:
                ApplyResolved(resolved);
                break;
        }
    }

    /// <inheritdoc />
    public void OnStreamEnd()
    {
        // 流结束不代表在途审批被清理（Manager 超时/决策为准）；保留卡片，交由下次 Bind/Reconcile 对账。
    }

    private void ApplyRequest(PermissionRequestEvent evt)
    {
        if (string.IsNullOrEmpty(evt.RequestId))
            return;
        if (string.IsNullOrEmpty(_sessionId) || !string.Equals(_sessionId, evt.SessionId, StringComparison.Ordinal))
            return;

        lock (_gate)
        {
            // 已决议：陈旧重放不得复活 pending。
            if (_cards.TryGetValue(evt.RequestId, out var existing) && !existing.IsPending)
                return;
        }

        // 对账：仅在 manager 仍视为在途时建卡（缓冲重放中已决议请求被过滤）。
        if (!IsStillPending(evt.SessionId, evt.RequestId))
            return;

        lock (_gate)
        {
            _cards[evt.RequestId] = new PermissionCardModel
            {
                RequestId = evt.RequestId,
                SessionId = evt.SessionId,
                CallId = evt.CallId,
                LoopId = evt.LoopId,
                PermissionKind = evt.PermissionKind,
                Resource = evt.Resource,
                Arguments = evt.Arguments,
                Message = evt.Message,
                RiskLevel = evt.RiskLevel,
                AllowedScopes = Normalize(evt.AllowedScopes),
                CreatedAt = ToOffset(evt.Timestamp),
                IsPending = true
            };
        }

        NotifyChanged();
    }

    private void ApplyResolved(PermissionResolvedEvent evt)
    {
        if (string.IsNullOrEmpty(evt.RequestId))
            return;
        if (!string.IsNullOrEmpty(evt.SessionId)
            && !string.Equals(_sessionId, evt.SessionId, StringComparison.Ordinal))
            return;

        var changed = false;
        lock (_gate)
        {
            // 未知 RequestId / 已决议：忽略（防陈旧重放）。
            if (_cards.TryGetValue(evt.RequestId, out var card) && card.IsPending)
            {
                card.IsPending = false;
                card.Decision = evt.Decision;
                card.Scope = evt.Scope;
                card.ResolvedBy = evt.ResolvedBy;
                card.Reason = evt.Reason;
                changed = true;
            }
        }

        if (changed)
            NotifyChanged();
    }

    private void LoadPending(string sessionId)
    {
        IReadOnlyList<PermissionRequest> pending;
        try
        {
            pending = _manager.GetPending(sessionId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "读取在途权限请求失败 SessionId={SessionId}", sessionId);
            return;
        }

        lock (_gate)
        {
            if (!string.Equals(_sessionId, sessionId, StringComparison.Ordinal))
                return;

            foreach (var request in pending)
            {
                if (string.IsNullOrEmpty(request.RequestId))
                    continue;
                // 已决议展示项不因重建而复活。
                if (_cards.TryGetValue(request.RequestId, out var existing) && !existing.IsPending)
                    continue;

                _cards[request.RequestId] = new PermissionCardModel
                {
                    RequestId = request.RequestId,
                    SessionId = request.SessionId,
                    CallId = request.CallId,
                    LoopId = request.LoopId,
                    PermissionKind = request.PermissionKind,
                    Resource = request.Resource,
                    Arguments = request.Arguments,
                    Message = request.Message,
                    RiskLevel = request.RiskLevel,
                    AllowedScopes = Normalize(request.AllowedScopes),
                    CreatedAt = request.CreatedAt,
                    IsPending = true
                };
            }
        }
    }

    private bool IsStillPending(string sessionId, string requestId)
    {
        try
        {
            return _manager.GetPending(sessionId)
                .Any(p => string.Equals(p.RequestId, requestId, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            // 读取失败时保守呈现（避免 UI 永久等待）；权威状态仍由 Manager 保证。
            _logger?.LogWarning(ex, "对账在途权限请求失败 RequestId={RequestId}", requestId);
            return true;
        }
    }

    private static IReadOnlyList<PermissionGrantScope> Normalize(IReadOnlyList<PermissionGrantScope>? scopes)
        => scopes is { Count: > 0 } ? scopes : DefaultScopes;

    private static DateTimeOffset ToOffset(DateTime timestamp)
        => timestamp.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Local))
            : new DateTimeOffset(timestamp);

    private void NotifyChanged()
    {
        if (_disposed)
            return;

        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "权限卡片变更通知失败");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Changed = null;
        lock (_gate)
            _cards.Clear();
    }
}

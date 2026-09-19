using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.WebUI.Models;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 全局权限收件箱（Singleton，只读投影，非权威）。
/// <para>
/// 输入：<see cref="IPermissionRequestManager.GetAllPending"/> + <see cref="IPermissionRequestManager.PendingChanged"/>；
/// 输出：<see cref="PermissionCardModel"/> 全量投影。每次通知全量重建（在途 ≤32），天然对账。
/// </para>
/// <para>并发契约：内部 <c>_gate</c> 串行写；<see cref="Changed"/> 锁外触发；不持有任何 Scoped 引用。</para>
/// </summary>
public sealed class PermissionInbox : IDisposable
{
    private static readonly IReadOnlyList<PermissionGrantScope> DefaultScopes =
        new[] { PermissionGrantScope.Once, PermissionGrantScope.Session };

    private readonly IPermissionRequestManager _manager;
    private readonly ILogger<PermissionInbox>? _logger;
    private readonly object _gate = new();

    private IReadOnlyList<PermissionCardModel> _cards = Array.Empty<PermissionCardModel>();
    private bool _disposed;

    public PermissionInbox(IPermissionRequestManager manager, ILogger<PermissionInbox>? logger = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _logger = logger;
        _manager.PendingChanged += OnPendingChanged;
        Rebuild();
    }

    /// <summary>投影变更通知（锁外触发）。</summary>
    public event Action? Changed;

    /// <summary>全部在途卡片快照（按创建时间升序）。</summary>
    public IReadOnlyList<PermissionCardModel> GetAll()
    {
        lock (_gate)
            return _cards;
    }

    /// <summary>指定会话的在途卡片快照。</summary>
    public IReadOnlyList<PermissionCardModel> GetBySession(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return Array.Empty<PermissionCardModel>();

        lock (_gate)
            return _cards
                .Where(c => string.Equals(c.SessionId, sessionId, StringComparison.Ordinal))
                .ToList();
    }

    /// <summary>指定工具调用关联的在途卡片快照。</summary>
    public IReadOnlyList<PermissionCardModel> GetByCallId(string? callId)
    {
        if (string.IsNullOrEmpty(callId))
            return Array.Empty<PermissionCardModel>();

        lock (_gate)
            return _cards
                .Where(c => string.Equals(c.CallId, callId, StringComparison.Ordinal))
                .ToList();
    }

    /// <summary>指定会话的在途卡片数。</summary>
    public int CountBySession(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return 0;

        lock (_gate)
            return _cards.Count(c => string.Equals(c.SessionId, sessionId, StringComparison.Ordinal));
    }

    /// <summary>全部在途卡片数。</summary>
    public int TotalCount
    {
        get { lock (_gate) return _cards.Count; }
    }

    private void OnPendingChanged() => Rebuild();

    private void Rebuild()
    {
        if (_disposed)
            return;

        IReadOnlyList<PermissionRequest> pending;
        try
        {
            pending = _manager.GetAllPending();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "读取全局在途权限请求失败");
            return;
        }

        var cards = pending
            .Where(p => !string.IsNullOrEmpty(p.RequestId))
            .OrderBy(p => p.CreatedAt)
            .Select(ToCard)
            .ToList();

        var changed = false;
        lock (_gate)
        {
            if (!SameIds(_cards, cards))
            {
                _cards = cards;
                changed = true;
            }
        }

        if (changed)
            NotifyChanged();
    }

    private static bool SameIds(
        IReadOnlyList<PermissionCardModel> current, IReadOnlyList<PermissionCardModel> next)
    {
        if (current.Count != next.Count)
            return false;

        for (var i = 0; i < current.Count; i++)
        {
            if (!string.Equals(current[i].RequestId, next[i].RequestId, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static PermissionCardModel ToCard(PermissionRequest request) => new()
    {
        RequestId = request.RequestId!,
        SessionId = request.SessionId,
        CallId = request.CallId,
        LoopId = request.LoopId,
        PermissionKind = request.PermissionKind,
        Resource = request.Resource,
        Arguments = request.Arguments,
        Message = request.Message,
        RiskLevel = request.RiskLevel,
        AllowedScopes = request.AllowedScopes is { Count: > 0 } ? request.AllowedScopes : DefaultScopes,
        CreatedAt = request.CreatedAt,
        IsPending = true
    };

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
            _logger?.LogWarning(ex, "权限收件箱变更通知失败");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _manager.PendingChanged -= OnPendingChanged;
        Changed = null;
        lock (_gate)
            _cards = Array.Empty<PermissionCardModel>();
    }
}

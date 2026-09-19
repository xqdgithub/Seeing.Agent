using Microsoft.Extensions.Logging;
using Seeing.Agent.Hosting.Web.Circuits;
using Seeing.Agent.WebUI.Models;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 当前标签（circuit）权限收件箱视图（Scoped，IDisposable）。
/// <para>
/// 作用域 = <see cref="SessionWindowRegistry.Windows"/> 的 SessionId 集合（唯一来源，已含子会话）；
/// 订阅 <see cref="PermissionInbox.Changed"/> 与 <see cref="SessionWindowRegistry.WindowsChanged"/>，
/// 经 <c>Interlocked</c> 单飞标志 + 短延时的微任务合并重算，锁外触发 <see cref="Changed"/>。
/// </para>
/// <para>prerender（空 circuitId）守卫：不订阅、不创建 registry、恒为空。</para>
/// </summary>
public sealed class PermissionInboxView : IDisposable
{
    private static readonly TimeSpan DefaultRecomputeDelay = TimeSpan.FromMilliseconds(50);

    private readonly PermissionInbox _inbox;
    private readonly Func<SessionWindowRegistry?> _registryAccessor;
    private readonly TimeSpan _recomputeDelay;
    private readonly ILogger<PermissionInboxView>? _logger;
    private readonly object _gate = new();

    private SessionWindowRegistry? _registry;
    private Action? _onInboxChanged;
    private Action? _onRegistryChanged;
    private IReadOnlyList<PermissionCardModel> _cards = Array.Empty<PermissionCardModel>();
    private int _dirty;
    private int _recomputeScheduled;
    private bool _enabled;
    private bool _disposed;

    public PermissionInboxView(
        PermissionInbox inbox,
        SessionEventStreamRouter router,
        CircuitContext circuitContext,
        ILogger<PermissionInboxView>? logger = null)
        : this(inbox, CreateAccessor(router, circuitContext), DefaultRecomputeDelay, logger)
    {
    }

    internal PermissionInboxView(
        PermissionInbox inbox,
        Func<SessionWindowRegistry?> registryAccessor,
        TimeSpan recomputeDelay,
        ILogger<PermissionInboxView>? logger = null)
    {
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        _registryAccessor = registryAccessor ?? throw new ArgumentNullException(nameof(registryAccessor));
        _recomputeDelay = recomputeDelay;
        _logger = logger;

        _registry = _registryAccessor();
        if (_registry is null)
            return; // prerender / 无 circuit：不订阅、不上报

        _enabled = true;
        _onInboxChanged = OnInboxChanged;
        _onRegistryChanged = OnRegistryChanged;
        _inbox.Changed += _onInboxChanged;
        _registry.WindowsChanged += _onRegistryChanged;
        RecomputeOnce();
    }

    private static Func<SessionWindowRegistry?> CreateAccessor(
        SessionEventStreamRouter router, CircuitContext circuitContext)
    {
        var circuitId = circuitContext.Id;
        if (string.IsNullOrEmpty(circuitId))
            return static () => null;
        return () => router.GetOrCreateCircuitConsumer<SessionWindowRegistry>(circuitId);
    }

    /// <summary>当前作用域内在途卡片快照。</summary>
    public IReadOnlyList<PermissionCardModel> CardsForScope
    {
        get { lock (_gate) return _cards; }
    }

    /// <summary>当前作用域内在途卡片总数。</summary>
    public int TotalCount
    {
        get { lock (_gate) return _cards.Count; }
    }

    /// <summary>投影变更通知（锁外触发；订阅组件在 InvokeAsync 内消费）。</summary>
    public event Action? Changed;

    public IReadOnlyList<PermissionCardModel> GetByCallId(string? callId)
    {
        if (string.IsNullOrEmpty(callId))
            return Array.Empty<PermissionCardModel>();

        lock (_gate)
            return _cards
                .Where(c => string.Equals(c.CallId, callId, StringComparison.Ordinal))
                .ToList();
    }

    public IReadOnlyList<PermissionCardModel> GetBySession(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return Array.Empty<PermissionCardModel>();

        lock (_gate)
            return _cards
                .Where(c => string.Equals(c.SessionId, sessionId, StringComparison.Ordinal))
                .ToList();
    }

    public int CountBySession(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return 0;

        lock (_gate)
            return _cards.Count(c => string.Equals(c.SessionId, sessionId, StringComparison.Ordinal));
    }

    /// <summary>该会话是否为子代理（task）子会话（经当前作用域窗口的关系判定）。</summary>
    public bool IsChildSession(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return false;

        var windows = _registry?.Windows;
        if (windows is null)
            return false;

        foreach (var window in windows)
        {
            if (string.Equals(window.SessionId, sessionId, StringComparison.Ordinal))
                return window.Relation == SessionRelation.Child;
        }

        return false;
    }

    private void OnInboxChanged() => ScheduleRecompute();

    private void OnRegistryChanged() => ScheduleRecompute();

    private void ScheduleRecompute()
    {
        if (_disposed || !_enabled)
            return;

        Volatile.Write(ref _dirty, 1);
        if (Interlocked.CompareExchange(ref _recomputeScheduled, 1, 0) == 1)
            return;

        _ = Task.Run(RunRecomputeAsync);
    }

    private async Task RunRecomputeAsync()
    {
        try
        {
            if (_recomputeDelay > TimeSpan.Zero)
                await Task.Delay(_recomputeDelay).ConfigureAwait(false);

            while (Interlocked.Exchange(ref _dirty, 0) == 1)
                RecomputeOnce();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "权限收件箱视图重算失败");
        }
        finally
        {
            Interlocked.Exchange(ref _recomputeScheduled, 0);
            if (!_disposed && Volatile.Read(ref _dirty) == 1)
                ScheduleRecompute();
        }
    }

    private void RecomputeOnce()
    {
        if (_disposed)
            return;

        var windows = _registry?.Windows;
        IReadOnlyList<PermissionCardModel> cards;
        if (windows is null || windows.Count == 0)
        {
            cards = Array.Empty<PermissionCardModel>();
        }
        else
        {
            var scope = new HashSet<string>(windows.Select(w => w.SessionId), StringComparer.Ordinal);
            cards = _inbox.GetAll().Where(c => scope.Contains(c.SessionId)).ToList();
        }

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
            _logger?.LogWarning(ex, "权限收件箱视图变更通知失败");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_enabled)
        {
            if (_onInboxChanged is not null)
                _inbox.Changed -= _onInboxChanged;
            if (_registry is not null && _onRegistryChanged is not null)
                _registry.WindowsChanged -= _onRegistryChanged;
        }

        Changed = null;
        lock (_gate)
            _cards = Array.Empty<PermissionCardModel>();
    }
}

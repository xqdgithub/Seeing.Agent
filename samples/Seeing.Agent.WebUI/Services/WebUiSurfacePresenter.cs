using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Hosting.Web.Circuits;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// WebUI circuit 呈现端（Scoped）：向 <see cref="IPermissionSurfaceRegistry"/> 声明
/// 当前标签（circuit）可呈现的会话集合。
/// <para>
/// <see cref="SurfaceSessionIds"/> 返回<b>已确认的稳定快照</b>（缓存字段），非实时读取；
/// 计算集合 = <see cref="SessionWindowRegistry.Windows"/> 的 SessionId ∪ AnchorSessionId；
/// 变化经短去抖（默认 250ms）合并后上报；空集候选经确认期（默认 1s）后才落定。
/// </para>
/// <para>prerender（空 circuitId）守卫：不注册、不创建 registry、不上报；Register/Unregister 幂等。</para>
/// </summary>
public sealed class WebUiSurfacePresenter : ISurfaceProvider, IDisposable
{
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultEmptyConfirm = TimeSpan.FromSeconds(1);

    private readonly IPermissionSurfaceRegistry _permissionStore;
    private readonly IQuestionSurfaceRegistry _questionStore;
    private readonly Func<SessionWindowRegistry?> _registryAccessor;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _emptyConfirm;
    private readonly ILogger<WebUiSurfacePresenter>? _logger;
    private readonly object _gate = new();
    private readonly HashSet<string> _confirmed = new(StringComparer.Ordinal);

    private SessionWindowRegistry? _registry;
    private Timer? _debounceTimer;
    private Timer? _emptyTimer;
    private bool _registered;
    private bool _disposed;

    public WebUiSurfacePresenter(
        IPermissionSurfaceRegistry permissionStore,
        IQuestionSurfaceRegistry questionStore,
        SessionEventStreamRouter router,
        CircuitContext circuitContext,
        ILogger<WebUiSurfacePresenter>? logger = null)
        : this(permissionStore, questionStore, CreateAccessor(router, circuitContext), DefaultDebounce, DefaultEmptyConfirm, logger)
    {
    }

    internal WebUiSurfacePresenter(
        IPermissionSurfaceRegistry permissionStore,
        IQuestionSurfaceRegistry questionStore,
        Func<SessionWindowRegistry?> registryAccessor,
        TimeSpan debounce,
        TimeSpan emptyConfirm,
        ILogger<WebUiSurfacePresenter>? logger = null)
    {
        _permissionStore = permissionStore ?? throw new ArgumentNullException(nameof(permissionStore));
        _questionStore = questionStore ?? throw new ArgumentNullException(nameof(questionStore));
        _registryAccessor = registryAccessor ?? throw new ArgumentNullException(nameof(registryAccessor));
        _debounce = debounce;
        _emptyConfirm = emptyConfirm;
        _logger = logger;

        _registry = _registryAccessor();
        if (_registry is null)
            return; // prerender / 无 circuit：不注册、不创建 registry

        _debounceTimer = new Timer(
            _ => OnDebounceElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _emptyTimer = new Timer(
            _ => OnEmptyConfirmElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _registry.WindowsChanged += OnRegistryChanged;

        var initial = ComputeSurface();
        lock (_gate)
        {
            if (initial.Count > 0)
                _confirmed.UnionWith(initial);
            else
                StartEmptyConfirmLocked();
        }

        _permissionStore.Register(this);
        _questionStore.Register(this);
        _registered = true;
    }

    private static Func<SessionWindowRegistry?> CreateAccessor(
        SessionEventStreamRouter router, CircuitContext circuitContext)
    {
        var circuitId = circuitContext.Id;
        if (string.IsNullOrEmpty(circuitId))
            return static () => null;
        return () => router.GetOrCreateCircuitConsumer<SessionWindowRegistry>(circuitId);
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

    private void OnRegistryChanged()
    {
        if (_disposed)
            return;

        bool empty;
        lock (_gate)
            empty = _confirmed.Count == 0;

        // 启动/首次填充：当前稳定快照仍为空时立即收敛，消除去抖空窗
        // （否则 Rebind 后 ~250ms 内新请求会因 CanSurface=false 被 fail-closed）。
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
        var registry = _registry;
        if (registry is null)
            return result;

        foreach (var window in registry.Windows)
            result.Add(window.SessionId);

        var anchor = registry.AnchorSessionId;
        if (!string.IsNullOrEmpty(anchor))
            result.Add(anchor);

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
            _logger?.LogWarning(ex, "呈现端 surface 变更通知失败");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_registry is not null)
            _registry.WindowsChanged -= OnRegistryChanged;

        _debounceTimer?.Dispose();
        _emptyTimer?.Dispose();
        _debounceTimer = null;
        _emptyTimer = null;

        if (_registered)
        {
            _registered = false;
            Unregister(_permissionStore);
            Unregister(_questionStore);
        }

        SurfacedChanged = null;
        lock (_gate)
            _confirmed.Clear();
    }

    private void Unregister(ISurfaceRegistry registry)
    {
        try
        {
            registry.Unregister(this);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "注销呈现端失败");
        }
    }
}

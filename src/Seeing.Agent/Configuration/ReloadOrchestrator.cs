using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Configuration;

/// <summary>
/// 统一重载编排器：订阅配置变更与工作区变更，按 IReloadHandler.ChangeTypes 路由分发
/// <para>串行执行、失败隔离、全量变更去抖合并、执行期间重入合并为一次待处理</para>
/// <para>实现 IReloadSignalBus（插件推送）与 IReloadHandlerRegistry（插件动态注册）</para>
/// </summary>
public sealed class ReloadOrchestrator : IReloadSignalBus, IReloadHandlerRegistry, IDisposable
{
    /// <summary>全量变更去抖窗口</summary>
    internal static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(150);

    private readonly ILogger<ReloadOrchestrator> _logger;
    private readonly IConfigSectionStore _configStore;
    private readonly IWorkspaceProvider _workspace;
    private readonly Dictionary<Type, List<IReloadHandler>> _routes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private bool _pendingFullReload;
    private bool _running;
    private bool _debouncePending;
    private bool _startedRound;
    private bool _disposed;

    public ReloadOrchestrator(
        IEnumerable<IReloadHandler> handlers,
        IConfigSectionStore configStore,
        IWorkspaceProvider workspace,
        ILogger<ReloadOrchestrator> logger)
    {
        _logger = logger;
        _configStore = configStore;
        _workspace = workspace;

        _routes = new Dictionary<Type, List<IReloadHandler>>();
        foreach (var handler in handlers)
            AddToRoutes(handler);

        _configStore.ConfigChanged += OnConfigChanged;
        _workspace.WorkspaceRootChanged += OnWorkspaceChanged;

        LogRegistrationSummary();
    }

    /// <summary>显式触发重载（供手动调用，如未来文件监视/CLI 命令；IReloadSignalBus.PublishAsync 即此方法）</summary>
    public async Task<IReadOnlyList<ReloadResult>> ReloadAsync(
        IReloadSignal change, CancellationToken ct = default)
    {
        var results = new List<ReloadResult>();

        IReloadHandler[]? snapshot;
        lock (_stateLock)
        {
            if (!_routes.TryGetValue(change.GetType(), out var handlers) || handlers.Count == 0)
            {
                snapshot = null;
            }
            else
            {
                // 防御性快照：避免 RegisterHandler/UnregisterHandler 并发修改导致迭代异常
                snapshot = handlers.ToArray();
            }
        }

        if (snapshot is null)
        {
            _logger.LogWarning("无组件响应变更类型: {ChangeType}", change.GetType().Name);
            return results;
        }

        await _gate.WaitAsync(ct);
        try
        {
            foreach (var handler in snapshot)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await handler.ReloadAsync(change, ct);
                    sw.Stop();
                    results.Add(new ReloadResult { ComponentId = handler.ComponentId, Success = true, Duration = sw.Elapsed });
                    _logger.LogDebug("重载完成: {ComponentId} ({Duration}ms)", handler.ComponentId, sw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    results.Add(new ReloadResult { ComponentId = handler.ComponentId, Success = false, Error = ex.Message, Duration = sw.Elapsed });
                    _logger.LogError(ex, "组件重载失败: {ComponentId}", handler.ComponentId);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return results;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ReloadResult>> PublishAsync(IReloadSignal signal, CancellationToken ct = default)
        => ReloadAsync(signal, ct);

    /// <inheritdoc/>
    public void RegisterHandler(IReloadHandler handler)
    {
        lock (_stateLock)
        {
            AddToRoutes(handler);
            _logger.LogInformation("动态注册重载处理器: {ComponentId}", handler.ComponentId);
        }
    }

    /// <inheritdoc/>
    public void UnregisterHandler(IReloadHandler handler)
    {
        lock (_stateLock)
        {
            // 防御性复制：ChangeTypes 可能是可变数组，避免迭代期间被外部修改
            foreach (var changeType in handler.ChangeTypes.ToArray())
            {
                if (_routes.TryGetValue(changeType, out var list))
                {
                    list.Remove(handler);
                    if (list.Count == 0) _routes.Remove(changeType);
                }
            }
            _logger.LogInformation("注销重载处理器: {ComponentId}", handler.ComponentId);
        }
    }

    private void AddToRoutes(IReloadHandler handler)
    {
        // 防御性复制：ChangeTypes 可能是可变数组，避免迭代期间被外部修改
        foreach (var changeType in handler.ChangeTypes.ToArray())
        {
            if (!_routes.TryGetValue(changeType, out var list))
                _routes[changeType] = list = new List<IReloadHandler>();
            if (!list.Contains(handler)) list.Add(handler);
        }
    }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        var isFull = e.ChangedSections.Length == 0;
        if (isFull)
        {
            EnqueueFullReload();
            return;
        }
        _ = ReloadAsync(new ConfigChange { ChangedSections = e.ChangedSections });
    }

    private void OnWorkspaceChanged(object? sender, WorkspaceChangedEventArgs e)
    {
        EnqueueFullReload();
    }

    /// <summary>全量变更去抖 + 执行期间重入合并</summary>
    private void EnqueueFullReload()
    {
        lock (_stateLock)
        {
            if (_running)
            {
                _pendingFullReload = true;   // 执行期间重入：合并为一次待处理
                return;
            }
            if (_debouncePending)
            {
                // 去抖等待期（本轮尚未执行）重入：合并进本次窗口
                // 收尾间隙（本轮已执行完但循环未退出）重入：置待处理，由循环收尾驱动下一轮，避免触发丢失
                if (_startedRound) _pendingFullReload = true;
                return;
            }
            _debouncePending = true;
            _startedRound = false;
        }

        _ = DebouncedFullReloadAsync();
    }

    private async Task DebouncedFullReloadAsync()
    {
        while (true)
        {
            try
            {
                await Task.Delay(DebounceWindow);   // 去抖窗口：合并连续全量触发

                lock (_stateLock)
                {
                    _running = true;
                    _startedRound = true;
                }
                try
                {
                    await ReloadAsync(new WorkspaceChange
                    {
                        OldWorkspace = _workspace.StartupDirectory,
                        NewWorkspace = _workspace.WorkspaceRoot
                    });

                    // 工作区切换场景：配置随工作区变更，需再执行一轮配置全量重载
                    await ReloadAsync(new ConfigChange { ChangedSections = Array.Empty<string>() });
                }
                finally
                {
                    lock (_stateLock) _running = false;
                }
            }
            catch (Exception ex)
            {
                // 防止异常导致去抖循环永久失效：记录并重置状态
                _logger.LogError(ex, "全量重载循环异常，终止本轮");
                lock (_stateLock)
                {
                    _debouncePending = false;
                    _startedRound = false;
                }
                return;
            }

            lock (_stateLock)
            {
                if (!_pendingFullReload)
                {
                    _debouncePending = false;
                    _startedRound = false;
                    break;
                }
                _pendingFullReload = false;
            }
        }
    }

    private void LogRegistrationSummary()
    {
        Dictionary<Type, IReloadHandler[]> snapshot;
        lock (_stateLock)
        {
            snapshot = _routes.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ToArray());
        }
        foreach (var (changeType, handlers) in snapshot)
        {
            _logger.LogInformation("重载注册: {ChangeType} <- {Handlers}",
                changeType.Name, string.Join(", ", handlers.Select(h => h.ComponentId)));
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _configStore.ConfigChanged -= OnConfigChanged;
        _workspace.WorkspaceRootChanged -= OnWorkspaceChanged;
        _gate.Dispose();
    }
}

/// <summary>
/// 触发编排器构造的宿主服务：ReloadOrchestrator 为惰性单例，
/// 需在宿主启动时显式解析一次，使其订阅配置/工作区变更事件
/// </summary>
internal sealed class ReloadOrchestratorStarter : IHostedService
{
    private readonly ReloadOrchestrator _orchestrator;

    public ReloadOrchestratorStarter(ReloadOrchestrator orchestrator) => _orchestrator = orchestrator;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _orchestrator.Dispose();
        return Task.CompletedTask;
    }
}
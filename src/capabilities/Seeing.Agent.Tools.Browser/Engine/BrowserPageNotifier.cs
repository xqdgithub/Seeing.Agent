using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Modules;

namespace Seeing.Agent.Tools.Browser;

/// <summary>
/// 浏览器页面可用通知总线：把「页面被创建」转达给当前所有 WS 观看端。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要这一层</b>：页面可能由 <b>agent 的工具</b>（<c>browser_open</c>）创建，
/// 而画面流订阅由 WS 端点建立。端点是 HTTP handler，生命周期按连接走，
/// 它无法在「连接建立前」就订阅页面事件；反过来，<see cref="BrowserSessionHost"/>
/// 也不知道当前有哪些 WS 连接。
/// </para>
/// <para>
/// 缺这一层的实际症状（实测缺陷）：agent 调 <c>browser_open</c> 后页面真的创建了、
/// 工具也真的能操作它（<c>browser_snapshot</c> 返回元素、<c>browser_click</c> 生效），
/// 但画布永远停在「浏览器尚未启动」的黑屏 —— 因为端点只在「连接建立」与
/// 「收到用户输入」时检查页面是否存在，agent 那条路径两个时机都不触发。
/// </para>
/// <para>
/// 实现是「按会话分组的回调登记 + 页面创建时广播」。回调方（WS 端点）负责编组到自己的连接上。
/// </para>
/// </remarks>
public sealed class BrowserPageNotifier : IHostedService, IModuleHostedService, IDisposable
{
    /// <summary>模块 id。</summary>
    /// <summary>模块 id（引用 <see cref="BrowserModule.IdValue"/>，避免第二份字面量漂移）。</summary>
    public const string ModuleIdValue = BrowserModule.IdValue;

    private readonly BrowserSessionHost _host;
    private readonly ILogger<BrowserPageNotifier>? _logger;

    /// <summary>按会话登记的回调（每个 WS 连接一个）。</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Func<BrowserPageHandle, Task>>> _listeners =
        new(StringComparer.Ordinal);

    private readonly object _gate = new();
    private bool _running;

    /// <summary>创建总线。</summary>
    /// <param name="host">会话页面宿主（页面创建事件来源）。</param>
    /// <param name="logger">日志。</param>
    public BrowserPageNotifier(BrowserSessionHost host, ILogger<BrowserPageNotifier>? logger = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _logger = logger;
    }

    /// <inheritdoc />
    public string ModuleId => ModuleIdValue;

    /// <inheritdoc />
    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _running;
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_running)
                return Task.CompletedTask;

            _host.PageCreated += OnPageCreated;
            _running = true;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_running)
                return Task.CompletedTask;

            _host.PageCreated -= OnPageCreated;
            _running = false;
            _listeners.Clear();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 订阅某会话的「页面可用」通知。返回的句柄释放时自动取消订阅。
    /// </summary>
    /// <param name="sessionId">会话 id。</param>
    /// <param name="callback">页面创建时回调（在页面创建线程上调用，实现方自行编组）。</param>
    public IDisposable Listen(string sessionId, Func<BrowserPageHandle, Task> callback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(callback);

        var id = Guid.NewGuid();
        var listeners = _listeners.GetOrAdd(sessionId, _ => new ConcurrentDictionary<Guid, Func<BrowserPageHandle, Task>>());
        listeners[id] = callback;

        return new Registration(this, sessionId, id);
    }

    private async Task OnPageCreated(string sessionId, BrowserPageHandle page)
    {
        if (!_listeners.TryGetValue(sessionId, out var listeners))
        {
            // 没有观看者不是错误：可能没人打开工作台。Debug 即可（Information 会刷屏）。
            _logger?.LogDebug("页面已创建但没有画面流订阅者：{SessionId}", sessionId);
            return;
        }

        foreach (var callback in listeners.Values)
        {
            try
            {
                await callback(page).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "转发页面创建通知失败（忽略该订阅者）：{SessionId}", sessionId);
            }
        }
    }

    /// <summary>
    /// 广播入口（测试用）：与 <see cref="OnPageCreated"/> 走同一条转发逻辑。
    /// </summary>
    /// <remarks>
    /// 生产路径由 <c>BrowserSessionHost.PageCreated</c> 驱动；这里是同一段代码的可测入口，
    /// 避免测试依赖私有反射（反射测试会在重构时静默失效）。
    /// </remarks>
    internal Task PublishForTestAsync(string sessionId, BrowserPageHandle? page)
        => OnPageCreated(sessionId, page!);

    private void Remove(string sessionId, Guid id)
    {
        if (_listeners.TryGetValue(sessionId, out var listeners))
            listeners.TryRemove(id, out _);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>为什么不用 <c>StopAsync().GetAwaiter().GetResult()</c></b>：那是仓库明令禁止的
    /// 「同步包装阻塞 async」——即使当前实现恰好是同步完成的，将来有人在 <c>StopAsync</c>
    /// 里加一个真正的 await（例如等待在途回调结束），这里就会变成死锁。
    /// 退订本身只是摘事件处理器，同步做即可，不需要走 async 路径。
    /// </remarks>
    public void Dispose() => Unsubscribe();

    private void Unsubscribe()
    {
        lock (_gate)
        {
            if (!_running)
                return;

            _host.PageCreated -= OnPageCreated;
            _running = false;
        }
    }

    private sealed class Registration : IDisposable
    {
        private readonly BrowserPageNotifier _owner;
        private readonly string _sessionId;
        private readonly Guid _id;
        private bool _disposed;

        public Registration(BrowserPageNotifier owner, string sessionId, Guid id)
        {
            _owner = owner;
            _sessionId = sessionId;
            _id = id;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _owner.Remove(_sessionId, _id);
        }
    }
}

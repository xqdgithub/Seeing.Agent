using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Modules;

namespace Seeing.Agent.Tools.Browser;

/// <summary>
/// 浏览器资源的进程级接线：把「会话页面关闭」事件接到画面流的释放上。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要这个托管服务</b>：<see cref="BrowserSessionHost"/> 与
/// <see cref="BrowserScreencastHub"/> 都是 DI 单例，谁也不是谁的构造函数依赖
/// （工具只用 host，端点两者都用）。若在端点的首个请求里做接线，接线时机取决于谁先被访问；
/// 若在某一个的构造函数里互相注入，又会形成循环依赖。
/// </para>
/// <para>
/// <b>不接的后果</b>：`browser_close`（或空闲回收）关掉页面后，画面流仍持有指向已关闭页面的
/// CDP 会话；同会话再次 `browser_open` 时复用旧流 → 画面永远停在最后一帧。
/// </para>
/// <para>
/// <b>形态与 <c>FileWatcherHostedService</c> 一致</b>：实现 <see cref="IHostedService"/> 与
/// <see cref="IModuleHostedService"/>，用 <c>AddModuleHostedService</c> 同时登记到
/// <c>IHostedService</c> / <c>IModuleHostedService</c> / 具体类型三个服务位——
/// 这样模块停用时 <c>IModuleCatalog</c> 门控与生命周期管理器都能看到它。
/// </para>
/// </remarks>
public sealed class BrowserLifetimeService : IHostedService, IModuleHostedService
{
    /// <summary>模块 id。</summary>
    /// <summary>模块 id（引用 <see cref="BrowserModule.IdValue"/>，避免第二份字面量漂移）。</summary>
    public const string ModuleIdValue = BrowserModule.IdValue;

    private readonly BrowserSessionHost _host;
    private readonly BrowserScreencastHub _hub;
    private readonly ILogger<BrowserLifetimeService> _logger;
    private readonly object _gate = new();

    private bool _running;

    /// <summary>创建接线服务。</summary>
    public BrowserLifetimeService(
        BrowserSessionHost host,
        BrowserScreencastHub hub,
        ILogger<BrowserLifetimeService>? logger = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<BrowserLifetimeService>.Instance;
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

            _host.PageClosed += OnPageClosed;
            _running = true;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        // 只撤接线，不 Dispose 单例：模块停用后可能被重新启用（热重载），
        // 单例一旦释放就永久不可用。这与 FileWatcherHostedService 的 StopAsync 同一条纪律。
        lock (_gate)
        {
            if (!_running)
                return Task.CompletedTask;

            _host.PageClosed -= OnPageClosed;
            _running = false;
        }

        return Task.CompletedTask;
    }

    private async Task OnPageClosed(string sessionId, BrowserPageHandle closedPage)
    {
        try
        {
            // 只清「指向被关闭页面」的那条流：close/reopen 交错时不能误杀新页面的流
            await _hub.DropSessionAsync(sessionId, closedPage).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "释放会话画面流失败（忽略）：{SessionId}", sessionId);
        }
    }
}

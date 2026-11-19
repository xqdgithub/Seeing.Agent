using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuppeteerSharp;

namespace Seeing.Agent.Tools.Browser;

/// <summary>会话页面来源：工具用这个端口拿页面，不直接依赖 Puppeteer 类型。</summary>
public interface IBrowserSessionHost
{
    /// <summary>
    /// 取该会话的页面；不存在则创建（含独立 <c>BrowserContext</c>）。
    /// <para>创建后立即导航到 <paramref name="initialUrl"/>（默认 <c>about:blank</c>）。</para>
    /// </summary>
    Task<BrowserPageHandle> GetPageAsync(
        string sessionId,
        string? initialUrl = null,
        CancellationToken cancellationToken = default);

    /// <summary>该会话当前是否已有页面。</summary>
    bool HasPage(string sessionId);

    /// <summary>取已有页面（不创建）；不存在返回 null。</summary>
    BrowserPageHandle? TryGetPage(string sessionId);

    /// <summary>关闭该会话的页面与隔离上下文。</summary>
    Task CloseAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>已打开会话的页面数量（诊断与测试用）。</summary>
    int OpenSessionCount { get; }

    /// <summary>
    /// 页面被创建时触发（供画面流建立订阅）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么必须有这个事件</b>：页面可能由 <b>agent 的工具</b>（<c>browser_open</c>）创建，
    /// 而画面流订阅是由 WS 端点建立的。端点只在「连接建立」与「收到用户输入」时检查页面是否存在，
    /// 因此 agent 打开页面后端点毫不知情 —— 结果是工具真的在操作页面，
    /// 但画布永远停在「尚未启动」的黑屏（实测缺陷）。
    /// </para>
    /// <para>与 <see cref="PageClosed"/> 对称：一个负责建流，一个负责拆流。</para>
    /// </remarks>
    event Func<string, BrowserPageHandle, Task>? PageCreated;

    /// <summary>判定地址是否可用于导航（与 agent 侧工具共用同一套策略与配置）。</summary>
    /// <remarks>
    /// <b>为什么放在端口上而不是让宿主直接读配置</b>：地址准入是能力包的规则
    /// （<c>BrowserUrlPolicy</c> + <c>BrowserOptions.AllowPrivateHosts</c> 的唯一判定点）。
    /// 宿主若为了拿配置而下转成实现类，既是「依赖实现细节」，也会在类型不匹配时静默退回默认值
    /// （表现为「配了 AllowPrivateHosts 却不生效」且看不出原因）。
    /// </remarks>
    /// <param name="url">目标地址。</param>
    BrowserUrlVerdict AdmitUrl(string? url);
}

/// <summary>
/// <see cref="IBrowserSessionHost"/> 的唯一实现：每会话一个页面，共享进程级 <see cref="BrowserEngine"/>。
/// </summary>
/// <remarks>
/// <para>
/// <b>上下文策略随 <see cref="BrowserOptions.PersistLogin"/> 切换</b>（实测结论，不能两全）：
/// </para>
/// <list type="bullet">
/// <item>
/// <b>开启（默认）</b>：全部会话共用 <c>DefaultContext</c>。这是 Chromium 里<b>唯一</b>会把
/// cookie 写入磁盘的上下文 —— <c>CreateBrowserContextAsync</c> 建的隔离上下文等同无痕窗口，
/// 实测「种下 cookie → 重启 → 0 个」。代价是 cookie 跨会话共享（这也正是登录态复用的前提）。
/// </item>
/// <item>
/// <b>关闭</b>：恢复每会话一个隔离上下文，cookie / 存储互不串场，但每次重启都是全新访客。
/// </item>
/// </list>
/// <para>
/// <b>为什么页面仍然每会话一个</b>：登录态共享不代表页面共享。每个会话有独立 <c>Page</c>
/// （独立 URL、独立 DOM、独立 ref 纪元），关闭一个会话不影响其它会话正在看的页面。
/// 实测默认上下文支持多页并存、关到零页浏览器也不退出。
/// </para>
/// <para>
/// <b>为什么每个页面是一个独立窗口而不是标签页</b>：Chromium 只给可见的标签页出
/// screencast 帧，后台标签页实测掉到 0 帧 —— 共用同一个浏览器时会有会话永远黑屏。
/// 建页统一走 <see cref="BrowserPageFactory"/>（<c>CreatePageType.Window</c>）。
/// </para>
/// <para>
/// <b>为什么空闲回收只关页面</b>：关掉进程会让下一次调用多付 200–400ms 冷启动；关页面已能释放
/// 绝大部分内存（渲染进程随之回收），进程留着复用。
/// </para>
/// </remarks>
public sealed class BrowserSessionHost : IBrowserSessionHost, IAsyncDisposable
{
    private readonly BrowserEngine _engine;
    private readonly IOptionsMonitor<BrowserOptions> _options;
    private readonly ILogger<BrowserSessionHost> _logger;
    private readonly TimeProvider _timeProvider;

    private readonly Dictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ITimer? _reaper;

    /// <summary>
    /// 本进程在宿主那里的「存在证明」（锁 + 页面归属清单）。
    /// </summary>
    /// <remarks>
    /// <b>懒创建（Ruling 6）</b>：只有真正要建页时才 <see cref="BrowserClientPresence.Start"/>。
    /// 在构造函数里创建会让「UI 常开但从未用过浏览器」也持有存活锁，宿主永远等不到空闲退出。
    /// 对象创建后随进程存活，锁句柄由 <see cref="BrowserClientPresence.AcquirePage"/> /
    /// <see cref="BrowserClientPresence.ReleasePage"/> 按页面计数增减。
    /// </remarks>
    private BrowserClientPresence? _presence;

    /// <summary>创建宿主。</summary>
    /// <param name="engine">进程级引擎。</param>
    /// <param name="options">配置。</param>
    /// <param name="logger">日志。</param>
    /// <param name="timeProvider">时钟（测试可推进虚拟时间）。</param>
    public BrowserSessionHost(
        BrowserEngine engine,
        IOptionsMonitor<BrowserOptions> options,
        ILogger<BrowserSessionHost>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<BrowserSessionHost>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // 每 30 秒扫一次；回收动作本身很轻（只有超时会关页面）
        _reaper = _timeProvider.CreateTimer(_ => _ = ReapAsync(), null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        // 连接丢失时页面句柄不会自动失效（PuppeteerSharp 的 Page.IsClosed 依赖
        // Target.detachedFromTarget，而断连收不到）。不订阅它，宿主崩溃/被杀后
        // 同一 sessionId 会一直命中指向死连接的页面，该会话的浏览器被永久锁死。
        _engine.ConnectionLost += OnConnectionLostAsync;
    }

    /// <inheritdoc />
    public int OpenSessionCount
    {
        get
        {
            lock (_sessions)
                return _sessions.Count;
        }
    }

    /// <summary>当前配置（端点做地址准入判定时需要与工具侧同一份口径）。</summary>
    public IOptionsMonitor<BrowserOptions> Options => _options;

    /// <inheritdoc />
    public BrowserUrlVerdict AdmitUrl(string? url)
        => BrowserUrlPolicy.Evaluate(url, _options.CurrentValue.AllowPrivateHosts);

    /// <inheritdoc />
    public bool HasPage(string sessionId) => TryGetPage(sessionId) is not null;

    /// <summary>
    /// 页面被关闭时触发（供画面流释放对应订阅）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>参数带页面实例，不只是会话 id</b>：close/reopen 是同一会话上的两次不同页面。若事件只带
    /// sessionId，一次「慢速关闭」可能在同会话已建好<b>新</b>页面并订阅之后才触发，
    /// 于是把新页面的画面流删掉——健康流被误杀，表现为「刚打开就黑屏且再无帧」。
    /// 订阅方据此比对实例，只清自己那一条。
    /// </para>
    /// <para>
    /// 不清理画面流会让下次打开同一会话时复用「指向已关闭页面」的流：CDP 会话已失效，
    /// 表现为「浏览器重新打开但画面永远不动」。
    /// </para>
    /// </remarks>
    public event Func<string, BrowserPageHandle, Task>? PageClosed;

    /// <inheritdoc />
    public event Func<string, BrowserPageHandle, Task>? PageCreated;

    /// <inheritdoc />
    public BrowserPageHandle? TryGetPage(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        lock (_sessions)
        {
            return _sessions.TryGetValue(sessionId, out var entry) && !entry.IsClosed
                ? entry.Page
                : null;
        }
    }

    /// <inheritdoc />
    public async Task<BrowserPageHandle> GetPageAsync(
        string sessionId,
        string? initialUrl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (TryGetPage(sessionId) is { } existing)
            return existing;

        BrowserPageHandle? handle = null;
        IBrowserContext? context = null;
        SessionEntry entry;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 双检：等锁期间可能已被另一路创建
            if (TryGetPage(sessionId) is { } raced)
                return raced;

            var options = _options.CurrentValue;
            var browser = await _engine.GetBrowserAsync(cancellationToken).ConfigureAwait(false);

            // 上下文策略见类型注释：默认上下文是唯一会把 cookie 写盘的上下文。
            // 用它就无法再传 DownloadBehavior（那是 BrowserContextOptions 的字段，只有隔离上下文可设），
            // 所以下载拒绝改由 BrowserEngine 在启动后用 CDP Browser.setDownloadBehavior 施加。
            IPage page;
            if (options.PersistLogin)
            {
                // DefaultContext 不是「每会话新建」，因此这里不持有可关闭的 context 引用：
                // 关闭会话只关页面，浏览器与 profile 继续存活（这正是登录态得以保留的原因）。
                page = await BrowserPageFactory.CreateAsync(browser, context: null, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                context = await browser.CreateBrowserContextAsync(new BrowserContextOptions
                {
                    DownloadBehavior = new DownloadBehavior { Policy = DownloadPolicy.Deny }
                }).ConfigureAwait(false);

                page = await BrowserPageFactory.CreateAsync(browser, context, cancellationToken)
                    .ConfigureAwait(false);
            }

            // 持久登录模式下页面建在默认上下文里，拿不到 BrowserContextOptions.DownloadBehavior，
            // 因此下载拒绝要在这里按页补一次（浏览器级已由 BrowserEngine 施加）。
            // 两级都设：页级覆盖面更精确，浏览器级兜住「页面在别处被创建」的情形。
            if (options.PersistLogin)
                await DenyDownloadsAsync(page).ConfigureAwait(false);

            await page.SetViewportAsync(new ViewPortOptions
            {
                Width = options.EffectiveWidth,
                Height = options.EffectiveHeight
            }).ConfigureAwait(false);

            // 反检测兜底必须在 SetViewportAsync **之后**：
            // 设置视口会重新初始化渲染环境，把先前注册的 OnNewDocument 脚本冲掉。
            // 顺序错了的症状是「UA 与 webdriver 看起来改对了，但 chrome.runtime 等注入全丢」——
            // 因为部分修补在脚本后半段，被冲掉后只剩前半段生效的假象。
            await _engine.ApplyAntiDetectionToPageAsync(page).ConfigureAwait(false);

            // 登记页面归属：宿主靠它回收「客户端崩溃后遗留的页面」。
            // targetId 取不到（极罕见）时仍要登记：存活锁比清单准确更重要 ——
            // 不持锁会让宿主认为「没有客户端」并空闲退出，把正在用的页面一起带走。
            // 用一个明确不可解析的占位 id，宿主 closeTarget 失败会被记日志，而不是静默丢掉页面。
            var targetId = await ResolveTargetIdAsync(page).ConfigureAwait(false);
            if (targetId is null)
            {
                targetId = "unknown-" + Guid.NewGuid().ToString("N");
                _logger.LogWarning(
                    "无法读取会话 {SessionId} 页面的 targetId，页面归属清单使用占位 id（宿主回收时会跳过）",
                    sessionId);
            }

            EnsurePresence().AcquirePage(targetId);

            handle = new BrowserPageHandle(sessionId, page, refEpoch: 0, _options,
                _logger is ILogger<BrowserSessionHost> hostLogger
                    ? new TypedLogger(hostLogger, sessionId)
                    : NullLogger.Instance);

            entry = new SessionEntry(handle, context, targetId);

            // 锁内只做字典交换，回收旧 entry 放到锁外 await：
            // 旧写法的 stale.DisposeQuietly() 在 lock 内做 .GetAwaiter().GetResult()，
            // 属于仓库明令禁止的「同步包装阻塞 async」+「锁内阻塞」双重反模式
            // （DisposeAsync 里还有 await _context.CloseAsync()，需要线程池线程）。
            SessionEntry? stale;
            lock (_sessions)
            {
                _sessions.Remove(sessionId, out stale);
                _sessions[sessionId] = entry;
            }

            if (stale is not null)
            {
                await stale.DisposeAsync().ConfigureAwait(false);
                stale.ReleasePresence(_presence);
            }
        }
        catch
        {
            // 创建/登记失败的资源必须回收，否则会永久留在浏览器进程里
            // （不报错、不可见，只在内存里慢慢堆积）。
            //
            // ⚠ 两件事都要做，且互相不可替代：
            // ① 关掉已创建的 page —— 持久登录模式下 page 建在 DefaultContext，
            //    没有可回收的 context，漏掉它就是「页面永久遗留在浏览器里」。
            // ② 关掉隔离 context（非持久模式）—— 它会连带关掉其下页面。
            if (handle is not null)
            {
                try { await handle.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogDebug(ex, "回收建页失败后的页面时出错（忽略）"); }
            }

            if (context is not null)
                await DisposeContextQuietlyAsync(context).ConfigureAwait(false);

            // presence 可能是本轮刚懒创建的：没登记上任何页面就把它收掉，
            // 否则那把它在 Start 时就抢到的存活锁会一直挂着，宿主永远不空闲退出。
            DisposePresenceIfUnused();

            throw;
        }
        finally
        {
            _gate.Release();
        }

        // 首次导航放在全局锁之外：一个 30 秒不响应的站点不该把「其它会话新建页面」一起堵住。
        // 页级互斥（BrowserPageHandle._gate）已保证同一页面不会被并发操作。
        // 单页策略必须在这次导航之前装上，否则第一页的 target=_blank 仍会开新标签。
        try
        {
            await handle.InstallSinglePagePolicyAsync(cancellationToken).ConfigureAwait(false);
            await handle.NavigateAsync(initialUrl ?? "about:blank", BrowserWaitUntil.DomContentLoaded, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // 导航失败：把这一条目撤掉并释放，避免留下「页面存在但没内容」的半成品
            await CloseAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        _logger.LogInformation("已为会话 {SessionId} 创建浏览器页面", sessionId);

        // 通知订阅方（画面流）：页面可能由 agent 的工具创建，端点自己不会知道。
        // 漏了这一步的症状是「工具在操作、画布永远黑屏」。
        await RaisePageCreatedAsync(sessionId, handle).ConfigureAwait(false);

        return handle;
    }

    /// <summary>
    /// 引擎到宿主的连接丢失：把所有页面句柄作废，并让画面流释放对应订阅。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么必须作废而不是等 <c>Page.IsClosed</c></b>：PuppeteerSharp 的
    /// <c>Page.IsClosed</c> 只在收到 <c>Target.detachedFromTarget</c> 后变 true；
    /// 宿主进程被杀时整条 CDP 连接直接消失，收不到任何 target 事件，于是
    /// 句柄<b>永远</b>看起来是活的。<c>TryGetPage</c> 命中它之后，后续工具调用全部
    /// 打在一个死连接上 —— 表现为「重启宿主后这个会话的浏览器再也用不了」。
    /// </para>
    /// <para>
    /// <b>为什么清理期间不持 <see cref="_gate"/></b>：<c>DisposeAsync</c> 内部可能 await
    /// CDP 操作（页面/上下文关闭），而 CDP 连接已经断了，这些调用可能很慢或抛异常。
    /// 持锁等待会让「新建页面」一起被卡住。因此锁内只做字典交换与清表。
    /// </para>
    /// <para>
    /// <b>为什么连 <c>PageClosed</c> 也发</b>：画面流持有指向旧页面的 CDP 会话；
    /// 不通知它，Hub 里会留下一条「有观众、无采集」的死流（画布永远停在最后一帧）。
    /// 集中器按页面实例比对，只清指向被作废页面的那条。
    /// </para>
    /// </remarks>
    private async Task OnConnectionLostAsync()
    {
        SessionEntry[] entries;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sessions)
            {
                entries = _sessions.Values.ToArray();
                _sessions.Clear();
            }
        }
        finally
        {
            _gate.Release();
        }

        if (entries.Length == 0)
            return;

        foreach (var entry in entries)
        {
            // 连接已死：Dispose 的每一次 CDP 调用都可能抛，且这里已经无法补救。
            // 吞掉异常继续处理其余页面 —— 留下一个未清理的 entry 字符串没有价值。
            try
            {
                await entry.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "连接丢失后回收页面失败（忽略）：{SessionId}", entry.Page.SessionId);
            }

            entry.ReleasePresence(_presence);
            await RaisePageClosedAsync(entry.Page.SessionId, entry.Page).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "浏览器宿主连接已断开，已作废 {Count} 个会话页面；下次使用同一会话时会自动重建页面",
            entries.Length);
    }

    /// <summary>通知订阅方：该会话有新页面可用（画面流应立即建立）。</summary>
    private async Task RaisePageCreatedAsync(string sessionId, BrowserPageHandle page)
    {
        var handler = PageCreated;
        if (handler is null)
            return;

        try
        {
            await handler(sessionId, page).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 通知失败不得影响页面创建本身：页面已经可用，只是画面流没接上
            _logger.LogDebug(ex, "通知页面创建失败（忽略）：{SessionId}", sessionId);
        }
    }

    /// <inheritdoc />
    public async Task CloseAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        SessionEntry? entry;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sessions)
            {
                if (!_sessions.Remove(sessionId, out entry))
                    entry = null;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (entry is not null)
        {
            var closedPage = entry.Page;
            await entry.DisposeAsync().ConfigureAwait(false);

            // 注销页面归属：1 → 0 时 presence 会释放存活锁（宿主据此判断能否空闲退出）。
            entry.ReleasePresence(_presence);

            _logger.LogInformation("已关闭会话 {SessionId} 的浏览器页面", sessionId);

            // 带上被关掉的那个页面实例：订阅方据此只清「指向它」的那条流，
            // 不会误杀同会话里已经新建好的另一条流。
            await RaisePageClosedAsync(sessionId, closedPage).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 懒创建本进程在宿主那里的存在证明（第一次真要建页时才调用，调用方持 <see cref="_gate"/>）。
    /// </summary>
    /// <remarks>
    /// 目录与宿主必须完全一致：profile 用宿主的固定解析，根目录用引擎解析的结果
    /// （含测试的 <c>SEEING_BROWSER_BASE_DIR</c> 覆盖）。两边各算一次会让宿主扫不到本进程，
    /// 于是它按「没有客户端」空闲退出，把别人的页面一起带走。
    /// </remarks>
    private BrowserClientPresence EnsurePresence()
        => _presence ??= BrowserClientPresence.Start(
            _engine.HostProfileDirectory,
            _engine.DiscoveryBaseDirectory,
            _logger);

    /// <summary>presence 里一个页面都没有时收掉它（失败路径专用，避免空持一把存活锁）。</summary>
    private void DisposePresenceIfUnused()
    {
        var presence = _presence;
        if (presence is null || presence.PageCount > 0)
            return;

        _presence = null;
        presence.Dispose();
    }

    /// <summary>
    /// 读取页面的 CDP targetId（页面归属清单的键）。
    /// </summary>
    /// <remarks>
    /// PuppeteerSharp 的 <c>ITarget</c> 不暴露 id，只能问 CDP。<see cref="IPage.Client"/> 是
    /// 页面自己的会话，读 <c>Target.getTargetInfo</c> 不会影响其它客户端。
    /// </remarks>
    private async Task<string?> ResolveTargetIdAsync(IPage page)
    {
        try
        {
            var response = await page.Client.SendAsync("Target.getTargetInfo").ConfigureAwait(false);
            if (response is { } payload
                && payload.TryGetProperty("targetInfo", out var info)
                && info.TryGetProperty("targetId", out var id)
                && id.GetString() is { Length: > 0 } targetId)
            {
                return targetId;
            }

            _logger.LogDebug("Target.getTargetInfo 未返回 targetId；该页面不会被登记到归属清单");
        }
        catch (Exception ex)
        {
            // 不影响建页：最坏情况是「客户端崩溃后宿主不会单独回收这一页」。
            _logger.LogDebug(ex, "读取页面 targetId 失败（页面归属清单将缺少这一项）");
        }

        return null;
    }

    /// <summary>通知订阅方：该会话的某个页面已关闭（画面流必须释放对应 CDP 会话）。</summary>
    private async Task RaisePageClosedAsync(string sessionId, BrowserPageHandle closedPage)
    {
        var handler = PageClosed;
        if (handler is null)
            return;

        try
        {
            await handler(sessionId, closedPage).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "通知页面关闭失败（忽略）：{SessionId}", sessionId);
        }
    }

    /// <summary>按页拒绝下载（默认上下文路径用；浏览器级另有一道）。</summary>
    private async Task DenyDownloadsAsync(IPage page)
    {
        try
        {
            var cdp = await page.CreateCDPSessionAsync().ConfigureAwait(false);
            await cdp.SendAsync("Page.setDownloadBehavior", new Dictionary<string, object>
            {
                ["behavior"] = "deny"
            }).ConfigureAwait(false);
            await cdp.DetachAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 不阻断建页：下载只是额外护栏，页面仍可正常使用。
            _logger.LogDebug(ex, "设置页级下载拒绝失败（忽略）");
        }
    }

    /// <summary>回收创建失败的上下文（不抛：清理失败不应盖住原始异常）。</summary>
    private async Task DisposeContextQuietlyAsync(IBrowserContext context)
    {
        try
        {
            await context.CloseAsync().ConfigureAwait(false);
            await context.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "回收创建失败的浏览器上下文时出错（忽略）");
        }
    }

    /// <summary>回收空闲会话的页面。</summary>
    private async Task ReapAsync()
    {
        if (_options.CurrentValue.IdleTimeout is not { } idle)
            return;

        var deadline = _timeProvider.GetUtcNow() - idle;
        List<string> expired;

        lock (_sessions)
        {
            expired = _sessions
                .Where(pair => pair.Value.Page.LastActivityAt < deadline || pair.Value.IsClosed)
                .Select(pair => pair.Key)
                .ToList();
        }

        foreach (var sessionId in expired)
        {
            try
            {
                await CloseAsync(sessionId).ConfigureAwait(false);
                _logger.LogDebug("空闲回收会话 {SessionId} 的浏览器页面", sessionId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "空闲回收会话 {SessionId} 失败（忽略）", sessionId);
            }
        }
    }

    /// <summary>
    /// 关闭全部会话页面但保持宿主可再次使用（模块停用 / 热重载路径）。
    /// </summary>
    /// <remarks>不释放内部信号量，理由同 <see cref="BrowserEngine.ShutdownAsync"/>。</remarks>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SessionEntry[] entries;
            lock (_sessions)
            {
                entries = _sessions.Values.ToArray();
                _sessions.Clear();
            }

            foreach (var entry in entries)
            {
                await entry.DisposeAsync().ConfigureAwait(false);
                entry.ReleasePresence(_presence);
            }

            // 模块停用后不能再占着客户端的存活锁：否则宿主会认为「还有客户端」而不退出。
            var presence = _presence;
            _presence = null;
            presence?.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _reaper?.Dispose();
        _engine.ConnectionLost -= OnConnectionLostAsync;

        SessionEntry[] entries;
        lock (_sessions)
        {
            entries = _sessions.Values.ToArray();
            _sessions.Clear();
        }

        foreach (var entry in entries)
        {
            await entry.DisposeAsync().ConfigureAwait(false);
            entry.ReleasePresence(_presence);
        }

        var presence = _presence;
        _presence = null;
        presence?.Dispose();

        _gate.Dispose();
    }

    /// <summary>一个会话的页面（及其隔离上下文，若上下文策略要求）。</summary>
    private sealed class SessionEntry : IAsyncDisposable
    {
        /// <summary>隔离上下文；持久登录模式下为 <see langword="null"/>（页面建在默认上下文里）。</summary>
        private readonly IBrowserContext? _context;
        private bool _closed;
        private bool _presenceReleased;

        public SessionEntry(BrowserPageHandle page, IBrowserContext? context, string? targetId)
        {
            Page = page;
            _context = context;
            TargetId = targetId;
        }

        public BrowserPageHandle Page { get; }

        /// <summary>CDP targetId（宿主回收遗留页面用）；读取失败时是占位 id。</summary>
        public string? TargetId { get; }

        public bool IsClosed => _closed || Page.Page.IsClosed;

        /// <summary>从客户端 presence 注销这一页（幂等：1 → 0 时释放存活锁）。</summary>
        public void ReleasePresence(BrowserClientPresence? presence)
        {
            if (_presenceReleased || TargetId is null)
                return;

            _presenceReleased = true;
            presence?.ReleasePage(TargetId);
        }

        public async ValueTask DisposeAsync()
        {
            if (_closed)
                return;

            _closed = true;

            try
            {
                await Page.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // 页面关闭失败不应阻断 context 清理
            }

            // 持久登录模式没有隔离上下文：只关页面，浏览器与 profile 必须继续存活
            // （关掉默认上下文等于关掉整个浏览器的 cookie 存储）。
            if (_context is null)
                return;

            try
            {
                await _context.CloseAsync().ConfigureAwait(false);
                await _context.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // 同上
            }
        }
    }

    /// <summary>把页面句柄日志限定到会话（诊断时能看出是哪个会话）。</summary>
    private sealed class TypedLogger : ILogger
    {
        private readonly ILogger _inner;
        private readonly string _sessionId;

        public TypedLogger(ILogger inner, string sessionId)
        {
            _inner = inner;
            _sessionId = sessionId;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _inner.Log(logLevel, eventId, state, exception,
                (s, ex) => $"[{_sessionId}] {formatter(s, ex)}");
    }
}

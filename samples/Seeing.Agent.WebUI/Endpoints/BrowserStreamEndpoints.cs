using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Tools.Browser;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Endpoints;

/// <summary>
/// 浏览器画面流端点：一帧下行 + 输入上行走同一条 WebSocket。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不走 Blazor circuit</b>：帧是二进制、每秒可达数十帧。塞进 circuit 会让服务端渲染
/// 全面卡顿，而且 SignalR 单消息大小也不适合 JPEG。
/// </para>
/// <para>
/// <b>鉴权基线</b>：与本机其它端点一致——会话必须存在、且地址必须落在会话自己身上。
/// 公网暴露需另加签名（已列入规格的已知边界）。
/// </para>
/// <para>
/// <b>输入与工具调用的并发</b>：两者都经 <see cref="BrowserPageHandle"/> 的会话级互斥串行执行，
/// 所以人工点击与 agent 操作不会交错破坏鼠标状态机。
/// </para>
/// </remarks>
public static class BrowserStreamEndpoints
{
    /// <summary>流路由模板。</summary>
    public const string Route = "/api/browser/{sessionId}/stream";

    /// <summary>宿主重启路由（设置页「重启浏览器」按钮）。</summary>
    public const string HostRestartRoute = "/browser/host/restart";

    /// <summary>单条上行消息上限（字节）：只承载输入事件，远小于帧；超限显式拒绝。</summary>
    private const int MaxInboundBytes = 1024 * 1024;

    /// <summary>单次 Receive 的分块大小（分片会被累积，这里只是读缓冲）。</summary>
    private const int ReceiveChunkBytes = 8 * 1024;

    /// <summary>
    /// 本端点所属能力模块 id。模块停用时端点必须返回 404。
    /// </summary>
    /// <remarks>
    /// 与 <c>BrowserModule.Id</c> 同值。这里不引用能力包的类型常量，是为了让「端点是否受门控」
    /// 这件事可以被单测直接验证（不需要构造 Puppeteer 依赖）。
    /// </remarks>
    public const string ModuleId = "browser";

    /// <summary>模块是否启用（端点门控的唯一判定点）。</summary>
    public static bool IsModuleEnabled(IModuleCatalog? modules)
        => modules?.IsEnabled(ModuleId) ?? false;

    /// <summary>映射端点。</summary>
    public static IEndpointRouteBuilder MapBrowserStreamEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, HandleAsync);
        endpoints.MapPost(HostRestartRoute, RestartHost);
        return endpoints;
    }

    /// <summary>
    /// 结束当前 profile 的浏览器宿主进程（设置页「重启浏览器」按钮）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么只结束宿主、不自己拉起新宿主</b>：新宿主由下一次 <c>browser_open</c> 按当时的配置自然拉起。
    /// 在这里顺手启动一个会带来两个问题——用户只是想让新设置「以后生效」，立刻占着浏览器窗口没有意义；
    /// 而且拉起是客户端的职责（要传 browser 级配置），端点没有那份上下文。
    /// </para>
    /// <para>
    /// <b>为什么必须校验进程身份</b>：发现文件可能是崩溃前的残留，PID 也可能已被系统复用。
    /// 只比对 PID 会杀掉无关进程；因此用 <see cref="BrowserHostLocator.IsSameHost"/>
    /// 同时比对「发现文件里的 PID + 启动时刻」与「实际进程的启动时刻」。
    /// </para>
    /// <para>
    /// <b>浏览器的去处</b>：宿主被强杀时，Windows 由 Job（KILL_ON_JOB_CLOSE）、POSIX 由看门狗
    /// 连带清掉 Chromium。端点自己不去碰浏览器进程。
    /// </para>
    /// <para>
    /// <b>信任边界</b>：与 CDP 端点一致——本机、无鉴权。任何本机进程本来就能直接控制浏览器，
    /// 这里不额外扩大暴露面。
    /// </para>
    /// </remarks>
    private static IResult RestartHost(
        IModuleCatalog modules,
        IOptionsMonitor<BrowserOptions> browserOptions,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("BrowserHostRestart");

        // 与画面流端点同一条门控：模块停用时能力必须整体消失。
        if (!IsModuleEnabled(modules))
            return Results.NotFound();

        var profile = BrowserHostLocator.NormalizeProfileDirectory(
            BrowserHostLocator.ResolveHostProfileDirectory(browserOptions.CurrentValue.ProfileDirectory));

        var locator = new BrowserHostLocator(profile, logger: logger);
        var info = locator.Read();
        if (info is null)
            return NoHostResult();

        Process process;
        try
        {
            process = Process.GetProcessById(info.HostPid);
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            // 进程已不在：发现文件是残留，交给下一次 browser_open 重新拉起。
            return NoHostResult();
        }

        using (process)
        {
            DateTimeOffset actualStartedAt;
            try
            {
                if (process.HasExited)
                    return NoHostResult();

                actualStartedAt = new DateTimeOffset(process.StartTime);
            }
            catch (Exception ex) when (ex is InvalidOperationException
                                           or System.ComponentModel.Win32Exception
                                           or NotSupportedException)
            {
                return NoHostResult();
            }

            if (!BrowserHostLocator.IsSameHost(info, info.HostPid, actualStartedAt))
            {
                // PID 被复用：绝不能杀一个碰巧拿到同一 PID 的无关进程。
                logger.LogWarning(
                    "发现文件与进程启动时刻不一致，拒绝结束进程 {Pid}（文件 {FileStartedAt}，实际 {ActualStartedAt}）",
                    info.HostPid, info.HostStartedAt, actualStartedAt);
                return Results.Json(new { ok = false, reason = "no host" });
            }

            try
            {
                // 只杀宿主本身：entireProcessTree=false 让 Job / watchdog 负责浏览器的生命周期，
                // 与「宿主被任务管理器强杀」走同一条恢复路径。
                process.Kill(entireProcessTree: false);
            }
            catch (Exception ex) when (ex is InvalidOperationException
                                           or System.ComponentModel.Win32Exception
                                           or NotSupportedException)
            {
                logger.LogWarning(ex, "结束浏览器宿主失败（pid={Pid}）", info.HostPid);
                return Results.Json(new { ok = false, reason = "kill failed" });
            }

            logger.LogInformation(
                "已按用户请求结束浏览器宿主（pid={Pid}，generation={Generation}），下次使用浏览器时重新拉起",
                info.HostPid, info.Generation);

            return Results.Json(new { ok = true });
        }
    }

    /// <summary>「没有可结束的宿主」的响应体（端点契约里的失败形态）。</summary>
    private static IResult NoHostResult()
        => Results.Json(new { ok = false, reason = "no host" });

    private static async Task HandleAsync(
        HttpContext context,
        string sessionId,
        ISessionManager sessions,
        IBrowserSessionHost host,
        BrowserScreencastHub hub,
        BrowserPageNotifier notifier,
        IModuleCatalog modules,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("BrowserStream");

        // 模块门控：模块停用（Modules.Disabled: ["browser"]）时这个端点必须不可达。
        // 否则「停用浏览器」只让工具与工作台消失，HTTP 面仍能连上并按需拉起 Chromium，
        // 破坏「模块停用即能力消失」这条架构不变量。
        if (!IsModuleEnabled(modules))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // 非 WebSocket 请求：明确 400（而不是让浏览器显示空白页）
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("该端点只接受 WebSocket 连接", ct).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(sessionId) || sessions.Get(sessionId) is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // 接管端口：能力包只产出「人工操作记录」，中断与告知是会话运行时的事，
        // 由宿主完成（能力包不依赖 ISessionRuntime，保持依赖方向干净）。
        var runtime = context.RequestServices.GetService<ISessionRuntime>();

        var page = host.TryGetPage(sessionId);
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);

        var sink = new WebSocketFrameSink(socket, logger);
        var sendGate = new SemaphoreSlim(1, 1);
        sink.Attach(sendGate, ct);

        // 订阅状态：页面可能在连接建立后才被创建（用户从地址栏输入网址启动浏览器），
        // 所以订阅不能在 Accept 前一次性完成 —— 否则空态提示的「在地址栏输入网址」
        // 永远走不通（历史缺陷：端点见无页面就直接关连接）。
        // 同时订阅必须可「重来」：browser_close 会清掉 Hub 里那条流，
        // 之后 browser_open 建的新页面需要重新订阅，否则画面永久冻结。
        IAsyncDisposable? subscription = null;
        BrowserPageHandle? subscribedPage = null;

        // EnsureSubscribedAsync 会被两条路径并发调用（WS 输入循环 + 页面创建通知），
        // 两个闭包同时改 subscription/subscribedPage 会重复订阅或泄漏。
        var ensureSubscribedGate = new SemaphoreSlim(1, 1);

        // 页面事件由页面自己通知（忙碌在持锁层置位；导航在 FrameNavigated）。
        // 调用方前后各推一次是错的——时机永远对不上。
        BrowserPageHandle? pageHooked = null;

        void OnBusyChanged(BrowserPageHandle changed) => hub.PublishState(sessionId, changed);

        // 只推状态，不重启 screencast。
        // 证据：BrowserEvidenceProbes.Screencast_ShouldKeepDeliveringFramesAfterNavigation_WithoutRestart
        // 证明跨文档导航后只要页面还有变化，原 screencast 仍出新帧；强制 Restart 是多余补丁。
        void OnNavigated(BrowserPageHandle changed) => hub.PublishState(sessionId, changed);

        void AttachBusy(BrowserPageHandle? target)
        {
            if (ReferenceEquals(pageHooked, target))
                return;

            if (pageHooked is not null)
            {
                pageHooked.BusyChanged -= OnBusyChanged;
                pageHooked.Navigated -= OnNavigated;
            }

            pageHooked = target;

            if (pageHooked is not null)
            {
                pageHooked.BusyChanged += OnBusyChanged;
                pageHooked.Navigated += OnNavigated;
            }
        }

        // agent 用工具（browser_open）建页面时，端点自己不会知道 —— 那两条检查时机
        // （连接建立 / 收到用户输入）都不触发。这里订阅页面创建通知，实现「工具一开页，
        // 画布立刻出画面」。这是「工具能用但画布永远黑屏」的直接解药。
        using var pageListener = notifier.Listen(sessionId, async created =>
        {
            // 不要把 ensureSubscribedGate 再取一次：EnsureSubscribedAsync 自己会拿它，
            // 同一信号量嵌套获取会永久阻塞（自死锁），表现为「页面创建了但画面流永远不建立」。
            await EnsureSubscribedAsync().ConfigureAwait(false);
            hub.PublishState(sessionId, created);
        });

        async Task EnsureSubscribedAsync()
        {
            await ensureSubscribedGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await EnsureSubscribedCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                ensureSubscribedGate.Release();
            }
        }

        async Task EnsureSubscribedCoreAsync()
        {
            var current = host.TryGetPage(sessionId);

            if (current is null)
            {
                if (subscribedPage is not null)
                {
                    // 页面没了：撤订阅并告知前端「未运行」，空态/遮罩才会回来
                    if (subscription is not null)
                    {
                        await subscription.DisposeAsync().ConfigureAwait(false);
                        subscription = null;
                    }

                    AttachBusy(null);
                    subscribedPage = null;

                    // 页面已没了：没有 ViewportWidth/FrameScale 可报，但代次仍要带上——
                    // 流对象还在（只是不再采集），丢掉代次会让前端把随后的新帧误判为旧代次。
                    // 0 的含义是「本会话从未有过画面流（未知）」，前端不得把 0 当权威代次比较。
                    sink.OnState(new BrowserPageState(
                        string.Empty, string.Empty, 0, false, false, hub.EpochOf(sessionId)));
                }

                return;
            }

            // 已订阅同一页面实例：无需重复（但忙碌事件要保证挂着）
            if (subscription is not null && ReferenceEquals(subscribedPage, current))
            {
                AttachBusy(current);
                return;
            }

            // 页面换了实例（重建）：旧订阅已无意义
            if (subscription is not null)
            {
                await subscription.DisposeAsync().ConfigureAwait(false);
                subscription = null;
            }

            subscription = await hub.SubscribeAsync(sessionId, current, sink, ct).ConfigureAwait(false);
            subscribedPage = current;
            AttachBusy(current);

            // 人回到这个会话、画面刚接上：此时还没有点击。
            // 把标签放到前台，并松开上次切走时可能没抬起的鼠标。
            // 不能拖到下一次 mousedown 里做 —— 那会把抬起事件从画布抢走，之后点什么都没反应。
            await current.ShowToUserAsync(ct).ConfigureAwait(false);
        }

        try
        {
            if (page is null)
            {
                // 先告知「尚未启动」，让前端显示空态而不是一直等首帧。
                // 代次照实带：0 = 本会话还没有过画面流（未知），不是「第 0 代」。
                sink.OnState(new BrowserPageState(
                    string.Empty, string.Empty, 0, false, false, hub.EpochOf(sessionId)));
            }
            else
            {
                await EnsureSubscribedAsync().ConfigureAwait(false);
            }

            await ReceiveLoopAsync(socket, sessionId, host, hub, runtime, sink, EnsureSubscribedAsync, logger, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 页面关闭 / circuit 取消：正常路径
        }
        catch (WebSocketException ex)
        {
            logger.LogDebug(ex, "浏览器流连接异常结束：{SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            // 兜底：任何未预期异常（CDP 会话创建失败、浏览器进程崩、订阅失败）都必须在这里停下，
            // 否则它会逸出 WebSocket 请求处理函数，前端只看到「连接出错」并进入重连循环，
            // 而每次重连都会踩同一个异常。这里记录并主动告知原因。
            logger.LogWarning(ex, "浏览器流会话异常结束：{SessionId}", sessionId);

            try
            {
                sink.OnError($"浏览器流已中断：{ex.Message}");
            }
            catch
            {
                // 连接已断，推不出去也没关系
            }
        }
        finally
        {
            // 先摘事件再撤订阅：否则页面在撤订阅期间触发 BusyChanged/Navigated，
            // 我们会对一个正在关闭的 sink 推状态
            if (pageHooked is not null)
            {
                pageHooked.BusyChanged -= OnBusyChanged;
                pageHooked.Navigated -= OnNavigated;
            }

            // 人离开这个会话时，画布不会再送来 mouseup。不补抬起的话，
            // 页面停在按下，回到这个会话后点击全部失效。
            if (pageHooked is not null)
            {
                try
                {
                    await pageHooked.ReleasePointerAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "离开会话时松开鼠标失败（忽略）：{SessionId}", sessionId);
                }
            }

            sink.Detach();

            if (subscription is not null)
            {
                try
                {
                    await subscription.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "释放画面订阅失败（忽略）：{SessionId}", sessionId);
                }
            }

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // 连接已断
                }
            }
        }
    }

    private static async Task ReceiveLoopAsync(
        WebSocket socket,
        string sessionId,
        IBrowserSessionHost host,
        BrowserScreencastHub hub,
        ISessionRuntime? runtime,
        WebSocketFrameSink sink,
        Func<Task> ensureSubscribed,
        ILogger logger,
        CancellationToken ct)
    {
        // 分片累积缓冲：WebSocket 消息可以拆成多帧（EndOfMessage == false）。
        // 旧实现把每次 ReceiveAsync 的结果当成一条完整消息，于是「粘贴一段超过 64KB 的文本」
        // 会被截断 → JsonDocument.Parse 抛异常 → 被吞成 Debug 日志 → 用户粘贴的内容静默丢失，
        // 且分片边界落在多字节字符中间时还会产生 U+FFFD 把原文改坏。
        var buffer = new byte[ReceiveChunkBytes];
        using var assembly = new MemoryStream();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
                return;

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            assembly.Write(buffer, 0, result.Count);

            if (assembly.Length > MaxInboundBytes)
            {
                // 显式拒绝而不是静默截断：客户端能拿到原因，不会以为「粘贴成功了」
                sink.OnError($"输入消息过大（上限 {MaxInboundBytes / 1024} KB）");
                await socket.CloseAsync(
                    WebSocketCloseStatus.MessageTooBig, "payload too large", ct).ConfigureAwait(false);
                return;
            }

            if (!result.EndOfMessage)
                continue;   // 还有后续分片

            var json = Encoding.UTF8.GetString(assembly.GetBuffer(), 0, (int)assembly.Length);
            assembly.SetLength(0);

            try
            {
                await HandleInputAsync(sessionId, host, hub, runtime, sink, ensureSubscribed, json, logger, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "处理浏览器输入失败：{SessionId}", sessionId);
            }
        }
    }

    private static async Task HandleInputAsync(
        string sessionId,
        IBrowserSessionHost host,
        BrowserScreencastHub hub,
        ISessionRuntime? runtime,
        WebSocketFrameSink sink,
        Func<Task> ensureSubscribed,
        string json,
        ILogger logger,
        CancellationToken ct)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;

        // 接管：先于一切输入处理。它不是浏览器操作，而是「停 agent + 告知」。
        if (string.Equals(type, "takeover", StringComparison.Ordinal))
        {
            await HandleTakeoverAsync(sessionId, host, hub, runtime, sink, logger, ct).ConfigureAwait(false);
            return;
        }

        // 导航可能创建页面：先处理它，其余输入都要求页面已存在
        if (string.Equals(type, "navigate", StringComparison.Ordinal))
        {
            var url = root.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(url))
                return;

            var verdict = host.AdmitUrl(url);
            if (!verdict.Allowed)
            {
                logger.LogDebug("地址栏导航被拒：{Reason}", verdict.Reason);
                sink.OnError(verdict.Reason ?? "地址不被允许");
                return;
            }

            var created = host.TryGetPage(sessionId) is null;

            var page = created
                ? await host.GetPageAsync(sessionId, null, ct).ConfigureAwait(false)
                : host.TryGetPage(sessionId)!;

            // 地址栏导航是**用户**的动作，走人工通道：不点亮「智能体正在操作」遮罩。
            // 走 agent 通道时，用户输完网址就被自己的遮罩挡住，连刚打开的页面都点不动。
            await page.NavigateAsUserAsync(url.Trim(), BrowserWaitUntil.DomContentLoaded, ct).ConfigureAwait(false);

            // 第一次导航后页面才存在：此刻补订阅，前端才会收到画面
            await ensureSubscribed().ConfigureAwait(false);
            PushState(hub, sink, page);
            return;
        }

        // 其余输入都作用于已有页面
        var current = host.TryGetPage(sessionId);
        if (current is null)
            return;

        // 自愈：页面被关过（Hub 那条流已被清掉）时，下一次输入会重新订阅，
        // 否则「关掉再打开」之后画布永久冻结（端点看不出区别：WS 仍连着）。
        await ensureSubscribed().ConfigureAwait(false);

        // 忙碌状态不在这里推：由 page.BusyChanged 在真正持锁的层触发（见 AttachBusy）。
        // 原先「前后各推一次」读到的都是复位后的值，遮罩从未亮过。
        switch (type)
        {
            case "mouse":
                await HandleMouseAsync(current, root, ct).ConfigureAwait(false);
                break;

            case "wheel":
                await current.ScrollByDeltaAsync(
                    ReadDouble(root, "deltaX"),
                    ReadDouble(root, "deltaY"),
                    ct).ConfigureAwait(false);
                break;

            case "key":
                await HandleKeyAsync(current, root, ct).ConfigureAwait(false);
                break;

            case "text":
                if (root.TryGetProperty("text", out var textElement)
                    && textElement.GetString() is { Length: > 0 } text)
                {
                    await current.TypeTextAsync(text, ct).ConfigureAwait(false);
                }
                break;

            case "reload":
                // 工具栏刷新是用户动作：走人工通道，不点亮遮罩
                await current.ReloadAsUserAsync(ct).ConfigureAwait(false);
                break;

            case "history":
                var direction = root.TryGetProperty("direction", out var dirElement) ? dirElement.GetString() : null;
                // 同上：前进/后退来自工作台工具栏，属人工动作
                if (string.Equals(direction, "back", StringComparison.Ordinal))
                    await current.BackAsUserAsync(BrowserWaitUntil.DomContentLoaded, ct).ConfigureAwait(false);
                else
                    await current.ForwardAsUserAsync(BrowserWaitUntil.DomContentLoaded, ct).ConfigureAwait(false);
                break;

            case "resize":
                var width = (int)ReadDouble(root, "width");
                var height = (int)ReadDouble(root, "height");
                // dpr 不取整：125% 屏上是 1.25，取整会让 DeviceScaleFactor 与前端像素比不符。
                // 合法范围与页面共用同一处判定，否则「服务端存 1、前端每条消息发 1.25」
                // 会让下面的判重永远为真 → 每条 resize 都重建 screencast。
                var pixelRatio = BrowserPageHandle.NormalizePixelRatio(ReadDouble(root, "pixelRatio"));

                // 尺寸或像素比变了才动：每次 resize 都要重建 CDP screencast（四个往返），
                // 前端去抖之外这里再挡一次，避免无意义的重建与画面闪烁。
                // 比的是「面板尺寸」而不是 ViewportWidth：超采样下实际视口 = 面板 × dpr。
                if (width >= 320 && height >= 240
                    && (width != current.RequestedWidth
                        || height != current.RequestedHeight
                        || Math.Abs(pixelRatio - current.PixelRatio) > 0.0001))
                {
                    logger.LogDebug(
                        "画面视口变更：{SessionId} {W}×{H} dpr={Dpr}（原 {CW}×{CH} dpr={CDpr}）",
                        sessionId, width, height, pixelRatio,
                        current.RequestedWidth, current.RequestedHeight, current.PixelRatio);
                    await current.ResizeAsync(width, height, pixelRatio, ct).ConfigureAwait(false);

                    // screencast 的 maxWidth/maxHeight 只在启动时生效；视口变了必须重启
                    // （StartCoreAsync 读的是页面**实际**视口）
                    await hub.RestartAsync(sessionId, ct).ConfigureAwait(false);

                    // 视口/帧缩放变了必须推一次 state：前端 backing 与坐标都读它，
                    // 不推就会停在旧尺寸（E2E 实测：帧已变 800×600，state 仍 1280×800）。
                    PushState(hub, sink, current);
                }
                else
                {
                    logger.LogDebug(
                        "忽略重复 resize：{SessionId} {W}×{H} dpr={Dpr}（当前 {CW}×{CH} dpr={CDpr}）",
                        sessionId, width, height, pixelRatio,
                        current.RequestedWidth, current.RequestedHeight, current.PixelRatio);
                }
                break;

            case "ping":
                // 保活：无需动作
                break;
        }
    }

    /// <summary>
    /// 处理「人工接管」：中断 agent 当前轮，并投递一条系统消息告知它已被接管。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么中断与告知分开做</b>：中断只让执行体停下，agent 的上下文里仍留着
    /// 「我正准备点下一个元素」的意图。不告知的话，用户下一次发话时它可能继续按原计划操作，
    /// 而页面已经被用户动过了。一条 <see cref="InboxPresentation.System"/> 消息把这件事说清楚。
    /// </para>
    /// <para>
    /// <b>为什么用 <see cref="InboxDelivery.Inject"/> 而不是 <see cref="InboxDelivery.Wake"/></b>：
    /// <c>Wake</c> 会立即激活一轮新执行 —— 用户刚说「我要自己操作」，
    /// 结果立刻又跑一轮 LLM 调用（还可能再去动浏览器），既浪费 token 也违背接管意图。
    /// <c>Inject</c> 只落盘，下次用户主动发话时它自然进入上下文。
    /// </para>
    /// <para>
    /// <b>诚实说明一个物理限制</b>：中断无法撤销「已经发出去的」CDP 指令。
    /// 若接管发生时那个工具正在执行，它的点击/输入可能仍然生效。这是能力边界，不是缺陷；
    /// 前端提示文案里会如实写明。
    /// </para>
    /// </remarks>
    private static async Task HandleTakeoverAsync(
        string sessionId,
        IBrowserSessionHost host,
        BrowserScreencastHub hub,
        ISessionRuntime? runtime,
        WebSocketFrameSink sink,
        ILogger logger,
        CancellationToken ct)
    {
        if (runtime is null)
        {
            // 没有会话运行时（嵌入式/测试宿主）：只提示，不假装成功
            sink.OnError("当前宿主未启用会话运行时，无法中断智能体");
            return;
        }

        bool interrupted;
        try
        {
            interrupted = await runtime
                .InterruptAsync(sessionId, ct, cancelBackgroundActivities: false)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "接管时中断执行失败：{SessionId}", sessionId);
            sink.OnError("中断智能体失败：" + ex.Message);
            return;
        }

        // 取出人工操作快照并清空：告知文本用它说明「页面为什么变了」，
        // 清空是为了下次接管不重复告知同一批动作。
        var actions = host.TryGetPage(sessionId)?.RecentHumanActions ?? [];
        host.TryGetPage(sessionId)?.ClearHumanActions();

        var body = BuildTakeoverNotice(actions);

        try
        {
            await runtime.DeliverAsync(new InboxMessage
            {
                TargetSessionId = sessionId,
                Sender = new InboxSender { Kind = InboxSenderKind.System, Id = "browser-takeover" },
                Body = body,
                Delivery = InboxDelivery.Inject,
                Presentation = InboxPresentation.System
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 告知失败不应让接管整体失败：中断已经成功，用户已经拿到控制权
            logger.LogDebug(ex, "接管告知投递失败（忽略）：{SessionId}", sessionId);
        }

        logger.LogInformation(
            "用户接管浏览器：会话 {SessionId}，中断{Interrupted}，人工操作 {Count} 条",
            sessionId, interrupted ? "成功" : "时无执行在跑", actions.Count);

        // 回一条状态给前端：让遮罩明确解除（而不是靠 busy 事件间接推断）
        var takeoverPage = host.TryGetPage(sessionId);
        sink.OnState(takeoverPage is null
            ? new BrowserPageState(string.Empty, string.Empty, 0, false, true, hub.EpochOf(sessionId))
            : hub.CreateState(takeoverPage, running: true) with { Busy = false });

        sink.OnTakeoverResult(interrupted, actions.Count);
    }

    /// <summary>构建告知 agent 的接管说明（最小信息量：被接管 + 当时在做什么）。</summary>
    private static string BuildTakeoverNotice(IReadOnlyList<BrowserHumanAction> actions)
    {
        var lines = new List<string>
        {
            $"[浏览器接管] 用户于 {DateTimeOffset.Now:HH:mm:ss} 手动接管了浏览器页面，" +
            "上一轮执行已被中断。",
            "请停止操作浏览器，等待用户明确指示。"
        };

        if (actions.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("用户此前的操作（供你理解页面为何已变化）：");
            foreach (var action in actions.TakeLast(10))
                lines.Add($"- {action.Render()}");
        }

        return string.Join('\n', lines);
    }

    private static async Task HandleMouseAsync(BrowserPageHandle page, JsonElement root, CancellationToken ct)
    {
        var eventName = root.TryGetProperty("event", out var eventElement) ? eventElement.GetString() : null;
        var x = ReadDouble(root, "x");
        var y = ReadDouble(root, "y");
        var button = root.TryGetProperty("button", out var buttonElement) ? buttonElement.GetString() : "left";

        switch (eventName)
        {
            case "move":
                await page.MouseMoveAsync(x, y, ct).ConfigureAwait(false);
                break;

            case "down":
                await page.MouseDownAsync(x, y, button, ct).ConfigureAwait(false);
                break;

            case "up":
                await page.MouseUpAsync(x, y, button, ct).ConfigureAwait(false);
                break;
        }
    }

    private static async Task HandleKeyAsync(BrowserPageHandle page, JsonElement root, CancellationToken ct)
    {
        var eventName = root.TryGetProperty("event", out var eventElement) ? eventElement.GetString() : null;
        var key = root.TryGetProperty("key", out var keyElement) ? keyElement.GetString() : null;

        if (string.IsNullOrEmpty(key))
            return;

        if (string.Equals(eventName, "down", StringComparison.Ordinal))
            await page.KeyDownAsync(key, ct).ConfigureAwait(false);
        else
            await page.KeyUpAsync(key, ct).ConfigureAwait(false);
    }

    /// <summary>把当前页面状态推给观看者（操作前后各推一次，遮罩才会跟着开关）。</summary>
    private static void PushState(
        BrowserScreencastHub hub,
        WebSocketFrameSink sink,
        BrowserPageHandle page)
        => sink.OnState(hub.CreateState(page));

    private static double ReadDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;

    /// <summary>
    /// 把 screencast 帧写进 WebSocket（二进制：18 字节头 + JPEG）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>背压</b>：帧走容量为 1 的 <see cref="Channel"/>（<see cref="BoundedChannelFullMode.DropOldest"/>），
    /// 由单消费者串行发送。慢客户端（后台标签页）因此不会让帧在内存里堆积——
    /// 新帧直接顶掉还没发出去的旧帧。丢帧是安全的：帧本身是幂等的画面快照，下一帧会覆盖。
    /// </para>
    /// <para>
    /// <b>为什么不用「正在写」标志位</b>：那个写法只能记住「有写在跑」，新帧到了只能丢，
    /// 于是慢客户端永远只拿到某一帧、后面的全丢（直到某一帧恰好写完）。
    /// Channel 的 DropOldest 语义是「保留最新的」，这才是画面流想要的。
    /// </para>
    /// <para>
    /// <b>为什么是二进制</b>：一帧 JPEG 转 base64 会膨胀 33%，外面再包一层 JSON 字符串，
    /// 宿主与前端各要做一次编解码。画面流对带宽和延迟都敏感。
    /// </para>
    /// </remarks>
    private sealed class WebSocketFrameSink : IBrowserFrameSink
    {
        private readonly WebSocket _socket;
        private readonly ILogger _logger;

        /// <summary>只保留最新一帧的发送队列（满时丢最旧的）。</summary>
        private readonly Channel<BrowserFrame> _frames = Channel.CreateBounded<BrowserFrame>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

        private SemaphoreSlim? _sendGate;
        private CancellationToken _ct = CancellationToken.None;
        private Task? _writer;

        public WebSocketFrameSink(WebSocket socket, ILogger logger)
        {
            _socket = socket;
            _logger = logger;
        }

        public void Attach(SemaphoreSlim sendGate, CancellationToken ct)
        {
            _sendGate = sendGate;
            _ct = ct;

            // 单消费者：帧的顺序由这里保证；状态/错误消息仍走 SendAsync（同一把 sendGate 串行）
            _writer ??= Task.Run(() => PumpAsync(ct));
        }

        public void Detach()
        {
            _sendGate = null;

            // 让消费者自然退出（不要在这里等：Detach 由请求收尾路径同步调用）
            _frames.Writer.TryComplete();
        }

        public void OnFrame(BrowserFrame frame)
        {
            // 非阻塞：CDP 回调线程上绝不能被网络发送拖住
            _frames.Writer.TryWrite(frame);
        }

        private async Task PumpAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var frame in _frames.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    try
                    {
                        await WriteFrameAsync(frame, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (WebSocketException ex)
                    {
                        _logger.LogDebug(ex, "写画面帧失败；连接可能已断开");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "写画面帧失败（忽略）");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 连接关闭：正常路径
            }
        }

        private async Task WriteFrameAsync(BrowserFrame frame, CancellationToken ct)
        {
            var gate = _sendGate;
            if (gate is null || _socket.State != WebSocketState.Open)
                return;

            // 编码在锁外做：编一帧是纯 CPU，不该占着发送锁
            var payload = BrowserFrameCodec.Encode(frame);

            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_socket.State != WebSocketState.Open)
                    return;

                await _socket.SendAsync(
                    new ArraySegment<byte>(payload),
                    WebSocketMessageType.Binary,
                    true,
                    ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        public void OnState(BrowserPageState state)
        {
            _ = SendAsync(
                new
                {
                    type = "state",
                    url = state.Url,
                    title = state.Title,
                    busy = state.Busy,
                    running = state.Running,
                    epoch = state.Epoch,
                    // 坐标与画布 backing 的权威来源（实际 SetViewport 的 CSS 尺寸）
                    viewportWidth = state.ViewportWidth,
                    viewportHeight = state.ViewportHeight,
                    // 帧倍率：帧像素 = 视口 × frameScale（默认 dpr）。前端画布 backing 用同一值。
                    frameScale = state.FrameScale
                },
                _ct);
        }

        public void OnError(string message)
        {
            _ = SendAsync(new { type = "error", message }, _ct);
        }

        /// <summary>推送接管结果（前端据此解除遮罩并提示）。</summary>
        public void OnTakeoverResult(bool interrupted, int actionCount)
        {
            _ = SendAsync(new { type = "takeover", interrupted, actionCount }, _ct);
        }

        private async Task SendAsync(object payload, CancellationToken ct)
        {
            var gate = _sendGate;
            if (gate is null || _socket.State != WebSocketState.Open)
                return;

            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (_socket.State != WebSocketState.Open)
                        return;

                    var json = JsonSerializer.Serialize(payload);
                    var bytes = Encoding.UTF8.GetBytes(json);
                    await _socket.SendAsync(
                        new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        true,
                        ct).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException ex)
            {
                _logger.LogDebug(ex, "写浏览器状态失败（忽略）");
            }
        }
    }
}



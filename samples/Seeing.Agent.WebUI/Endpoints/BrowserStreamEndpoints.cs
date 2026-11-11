using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
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
        return endpoints;
    }

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

        // 忙碌状态由页面自己通知（它在持锁的那一层置位）；调用方前后各推一次是错的 ——
        // 那时 busy 还没置位 / 已经复位，推出去永远是「不忙」，遮罩成了死代码。
        BrowserPageHandle? busyHooked = null;

        void OnBusyChanged(BrowserPageHandle changed) => hub.PublishState(sessionId, changed);

        void AttachBusy(BrowserPageHandle? target)
        {
            if (ReferenceEquals(busyHooked, target))
                return;

            if (busyHooked is not null)
                busyHooked.BusyChanged -= OnBusyChanged;

            busyHooked = target;

            if (busyHooked is not null)
                busyHooked.BusyChanged += OnBusyChanged;
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
                    sink.OnState(new BrowserPageState(string.Empty, string.Empty, 0, false, false));
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
        }

        try
        {
            if (page is null)
            {
                // 先告知「尚未启动」，让前端显示空态而不是一直等首帧
                sink.OnState(new BrowserPageState(string.Empty, string.Empty, 0, false, false));
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
            // 先摘事件再撤订阅：否则页面在撤订阅期间触发 BusyChanged，
            // 我们会对一个正在关闭的 sink 推状态
            if (busyHooked is not null)
                busyHooked.BusyChanged -= OnBusyChanged;

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
            await HandleTakeoverAsync(sessionId, host, runtime, sink, logger, ct).ConfigureAwait(false);
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

            await page.NavigateAsync(url.Trim(), BrowserWaitUntil.DomContentLoaded, ct).ConfigureAwait(false);

            // 第一次导航后页面才存在：此刻补订阅，前端才会收到画面
            await ensureSubscribed().ConfigureAwait(false);
            PushState(sink, page);
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
                await current.ReloadAsync(ct).ConfigureAwait(false);
                break;

            case "history":
                var direction = root.TryGetProperty("direction", out var dirElement) ? dirElement.GetString() : null;
                if (string.Equals(direction, "back", StringComparison.Ordinal))
                    await current.BackAsync(BrowserWaitUntil.DomContentLoaded, ct).ConfigureAwait(false);
                else
                    await current.ForwardAsync(BrowserWaitUntil.DomContentLoaded, ct).ConfigureAwait(false);
                break;

            case "resize":
                var width = (int)ReadDouble(root, "width");
                var height = (int)ReadDouble(root, "height");
                var pixelRatio = (int)Math.Round(ReadDouble(root, "pixelRatio"));
                if (pixelRatio < 1)
                    pixelRatio = 1;

                // 尺寸或像素比变了才动：每次 resize 都要重建 CDP screencast（四个往返），
                // 前端去抖之外这里再挡一次，避免无意义的重建与画面闪烁。
                if (width >= 320 && height >= 240
                    && (width != current.ViewportWidth
                        || height != current.ViewportHeight
                        || pixelRatio != current.PixelRatio))
                {
                    await current.ResizeAsync(width, height, pixelRatio, ct).ConfigureAwait(false);

                    // screencast 的 maxWidth/maxHeight 只在启动时生效；视口变了必须重启
                    // （RestartAsync 内部读的是页面**实际**视口 × 像素比）
                    await hub.RestartAsync(sessionId, ct).ConfigureAwait(false);
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
            interrupted = await runtime.InterruptAsync(sessionId, ct).ConfigureAwait(false);
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
        sink.OnState(new BrowserPageState(
            host.TryGetPage(sessionId)?.CurrentUrl ?? string.Empty,
            host.TryGetPage(sessionId)?.CurrentTitle ?? string.Empty,
            0, false, true));

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

    private static BrowserPageState StateOf(BrowserPageHandle page, bool running = true)
        => new(page.CurrentUrl, page.CurrentTitle, page.RefEpoch, page.IsBusy, running);

    /// <summary>把当前页面状态推给观看者（操作前后各推一次，遮罩才会跟着开关）。</summary>
    private static void PushState(WebSocketFrameSink sink, BrowserPageHandle page)
        => sink.OnState(StateOf(page));

    private static double ReadDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;

    /// <summary>
    /// 把 screencast 帧写进 WebSocket。
    /// </summary>
    /// <remarks>
    /// <b>背压</b>：写操作串行化，且「上一帧还没写完就丢弃新帧」。慢客户端（后台标签页）因此
    /// 不会让帧在内存里堆积。丢帧是安全的：帧本身是幂等的画面快照，下一帧会覆盖。
    /// </remarks>
    private sealed class WebSocketFrameSink : IBrowserFrameSink
    {
        private readonly WebSocket _socket;
        private readonly ILogger _logger;

        private SemaphoreSlim? _sendGate;
        private CancellationToken _ct;
        private int _writing;
        private long _dropped;

        public WebSocketFrameSink(WebSocket socket, ILogger logger)
        {
            _socket = socket;
            _logger = logger;
        }

        public void Attach(SemaphoreSlim sendGate, CancellationToken ct)
        {
            _sendGate = sendGate;
            _ct = ct;
        }

        public void Detach() => _sendGate = null;

        public void OnFrame(BrowserFrame frame)
        {
            // 已有发送在进行：丢这一帧（下一帧是新画面，丢弃无损）
            if (Interlocked.CompareExchange(ref _writing, 1, 0) != 0)
            {
                _dropped++;
                return;
            }

            _ = SendFrameAsync(frame);
        }

        private async Task SendFrameAsync(BrowserFrame frame)
        {
            try
            {
                var gate = _sendGate;
                if (gate is null || _socket.State != WebSocketState.Open)
                    return;

                await gate.WaitAsync(_ct).ConfigureAwait(false);
                try
                {
                    if (_socket.State != WebSocketState.Open)
                        return;

                    var json = JsonSerializer.Serialize(new
                    {
                        type = "frame",
                        seq = frame.Seq,
                        mime = "image/jpeg",
                        data = Convert.ToBase64String(frame.Data),
                        width = frame.Width,
                        height = frame.Height
                    });

                    var bytes = Encoding.UTF8.GetBytes(json);
                    await _socket.SendAsync(
                        new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        true,
                        _ct).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // 连接关闭
            }
            catch (WebSocketException ex)
            {
                _logger.LogDebug(ex, "写画面帧失败；连接可能已断开");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "写画面帧失败（忽略）");
            }
            finally
            {
                Interlocked.Exchange(ref _writing, 0);
            }
        }

        public void OnState(BrowserPageState state)
        {
            _ = SendAsync(
                new { type = "state", url = state.Url, title = state.Title, busy = state.Busy, running = state.Running },
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



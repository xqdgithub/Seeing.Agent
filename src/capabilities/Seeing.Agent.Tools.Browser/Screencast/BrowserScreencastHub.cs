using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuppeteerSharp;

namespace Seeing.Agent.Tools.Browser;

/// <summary>
/// 每会话的画面流中心：一个 CDP screencast，多观看者广播，输入注入。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么一个会话只开一份 screencast</b>：CDP 的 <c>Page.startScreencast</c> 是页级单例，
/// 重复 start 会互相顶掉。多个 circuit（多个标签页）同时看同一会话时必须广播同一份帧。
/// </para>
/// <para>
/// <b>为什么必须有 ack</b>：Chromium 收到 <c>screencastFrameAck</c> 才推下一帧。漏 ack 的表现是
/// 「画面永远停在第一帧」——已实测确认。
/// </para>
/// <para>
/// <b>为什么必须有背压</b>：不丢帧的话，一个慢客户端（浏览器标签页被切到后台、网络抖动）会让
/// 帧在内存里堆积，几分钟就能吃掉几百 MB。<see cref="FramePump"/> 只保留最新帧。
/// </para>
/// </remarks>
public sealed class BrowserScreencastHub : IAsyncDisposable
{
    /// <summary>JPEG 质量（60 在文字可读与带宽之间取平衡）。</summary>
    /// <summary>
    /// JPEG 质量。
    /// </summary>
    /// <remarks>
    /// 60 在文字密集页面上明显发虚（用户报「分辨率很低」）。
    /// 80 是「文字边缘清晰」与「帧大小」的平衡点：1280×800 下约 +35% 体积，
    /// 但字能看清 —— 画面存在的意义就是让人看清内容，不是省带宽。
    /// 注意：CDP screencast 的 <c>maxWidth/maxHeight</c> 必须等于真实视口，
    /// 否则浏览器会先等比缩小再编码，质量再高也救不回来。
    /// </remarks>
    private const int JpegQuality = 80;

    /// <summary>最后一个观看者离开后，延后多久停止 screencast（避免刷新页面导致反复启停）。</summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, SessionStream> _streams = new(StringComparer.Ordinal);
    private readonly IOptionsMonitor<BrowserOptions> _options;
    private readonly ILogger<BrowserScreencastHub> _logger;

    /// <summary>创建中心。</summary>
    /// <param name="options">配置。</param>
    /// <param name="logger">日志。</param>
    public BrowserScreencastHub(IOptionsMonitor<BrowserOptions> options, ILogger<BrowserScreencastHub>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<BrowserScreencastHub>.Instance;
    }

    /// <summary>当前有观看者的会话数（诊断用）。</summary>
    public int ActiveSessionCount => _streams.Count;

    /// <summary>
    /// 订阅某会话的画面。返回的句柄释放时取消订阅；最后一个订阅者离开后延后停止 screencast。
    /// </summary>
    /// <param name="sessionId">会话 id。</param>
    /// <param name="page">该会话页面（调用方保证非空；页面重建后需重新订阅）。</param>
    /// <param name="sink">帧接收方。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IAsyncDisposable> SubscribeAsync(
        string sessionId,
        BrowserPageHandle page,
        IBrowserFrameSink sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(sink);

        var stream = _streams.AddOrUpdate(
            sessionId,
            _ => new SessionStream(this, sessionId),
            (_, existing) => existing);

        var subscription = await stream.AddViewerAsync(page, sink, cancellationToken).ConfigureAwait(false);
        return subscription;
    }

    /// <summary>
    /// 页面视口变化后重启 screencast（使帧尺寸跟随新视口）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// CDP screencast 的 <c>maxWidth</c>/<c>maxHeight</c> 是<b>启动时</b>指定的，之后不会跟随视口变化。
    /// 面板拖宽后视口变大而 screencast 仍按旧尺寸输出，画面就会被拉伸（文字发虚）。
    /// </para>
    /// <para>没有观看者时什么都不做：下次订阅会用新尺寸启动。</para>
    /// </remarks>
    public async Task RestartAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        if (!_streams.TryGetValue(sessionId, out var stream))
            return;

        await stream.RestartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 按页面当前实际状态构造一条状态快照（视口/帧倍率/模式都取自实际页面，不读配置视口）。
    /// </summary>
    /// <remarks>
    /// <b>为什么集中在一处</b>：状态有多个发布点（订阅、忙碌、导航、接管、重建失败）。
    /// 只要有一处漏填新字段，前端就会收到 <c>viewportWidth=0</c> 并把画布 backing 清成 0
    /// （表现为「操作一下画面就没了」）。所有发布点都必须走这里。
    /// </remarks>
    public BrowserPageState CreateState(BrowserPageHandle page, bool running = true)
    {
        ArgumentNullException.ThrowIfNull(page);

        return new BrowserPageState(
            page.CurrentUrl,
            page.CurrentTitle,
            page.RefEpoch,
            page.IsBusy,
            running,
            EpochOf(page.SessionId),
            page.ViewportWidth,
            page.ViewportHeight,
            page.FrameScale,
            _options.CurrentValue.HighResStream ? "highres" : "normal");
    }

    /// <summary>
    /// 向该会话的所有观看者广播一次页面状态（操作前后各调一次，前端遮罩才跟着开关）。
    /// </summary>
    /// <remarks>
    /// 没有流（还没人看）时静默返回：这不是错误，只是没人订阅。
    /// </remarks>
    public void PublishState(string sessionId, BrowserPageHandle page)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        if (!_streams.TryGetValue(sessionId, out var stream))
            return;

        // Running 恒为 true：本方法只在「有页面且正在推状态」时被调用
        // （调用方都先确认过 page 存在）。页面关闭走的是 DropSessionAsync 那条路径，
        // 那里推的是 running=false。
        stream.PublishState(CreateState(page));
    }

    /// <summary>
    /// 取某会话当前的画面代次；没有流时为 <c>0</c>。
    /// </summary>
    /// <remarks>
    /// 端点的状态消息要带代次（与帧头同源），但它拿不到 <see cref="SessionStream"/>。
    /// 这里只暴露「读」，代次的推进仍由流的启停独占。
    /// </remarks>
    public uint EpochOf(string sessionId)
        => !string.IsNullOrWhiteSpace(sessionId) && _streams.TryGetValue(sessionId, out var stream)
            ? stream.Epoch
            : 0u;

    /// <summary>
    /// 抓一张当前视口静帧；不适合抓时返回 <see langword="null"/>。
    /// </summary>
    /// <param name="sessionId">会话 id。</param>
    /// <param name="rev">代次内的版本号（原样写进帧头的 seq 字段）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// <para>
    /// <b>为什么返回帧而不是直接广播</b>：静帧是「谁请求谁需要」——只有发起请求的那个观看者
    /// 正在滚动/静止，把一张 1~2 MB 的高清图推给同会话的其他观看者纯属浪费。
    /// </para>
    /// <para>
    /// <b>为什么 <see cref="BrowserOptions.HighResStream"/> 开启时直接返回 null</b>：
    /// 此时流本身就是全分辨率，再抓一张没有增益，只会多一次 CDP 往返。
    /// </para>
    /// </remarks>
    public async Task<BrowserFrame?> TryCaptureStillAsync(
        string sessionId,
        long rev,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        if (_options.CurrentValue.HighResStream)
            return null;

        if (!_streams.TryGetValue(sessionId, out var stream))
            return null;

        return await stream.CaptureStillAsync(rev, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 页面被关闭时清掉对应流（避免残留 screencast 会话）。
    /// </summary>
    /// <param name="sessionId">会话 id。</param>
    /// <param name="page">
    /// 被关闭的那个页面。只清「当前流指向它」的那条；
    /// 若传 null 则无条件清（模块停用/关停路径）。
    /// </param>
    /// <remarks>
    /// <b>为什么要比对页面实例</b>：`close` 与 `reopen` 可能交错——一次慢速关闭的完成事件
    /// 可能在同会话已建好新页面并订阅之后才到。只按 sessionId 清会把**新**页面的健康流删掉，
    /// 表现为「刚打开就黑屏、再无帧」。
    /// </remarks>
    public async Task DropSessionAsync(string sessionId, BrowserPageHandle? page = null)
    {
        if (!_streams.TryGetValue(sessionId, out var stream))
            return;

        if (page is not null && !ReferenceEquals(stream.PageHandle, page))
            return;

        if (_streams.TryRemove(sessionId, out var removed))
            await removed.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 停止全部画面流但保持实例可再次使用（模块停用 / 热重载路径）。
    /// </summary>
    /// <remarks>
    /// <b>为什么与 <see cref="DisposeAsync"/> 分开</b>：本类是 DI 单例，模块停用后可能被重新启用。
    /// <c>DisposeAsync</c> 属于容器语义（此后实例不应再用）；<c>ShutdownAsync</c> 只停资源。
    /// 目前两者实现相近，但语义必须分开——若将来有人在 <c>DisposeAsync</c> 里补一句
    /// <c>_gate.Dispose()</c>（完全符合 IDisposable 直觉），而模块停用走的是它，
    /// 就会复现「停用再启用后永久不可用」这类缺陷。
    /// </remarks>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        var streams = _streams.Values.ToArray();
        _streams.Clear();

        foreach (var stream in streams)
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "停止画面流失败（忽略）");
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);

        // 不释放任何信号量：本实例可能被容器在极晚时释放，而流内部的延迟停止任务
        // 仍可能持锁；Dispose 后取锁会抛 ObjectDisposedException（无法 observe 的位置）。
    }

    /// <summary>一个会话的画面流（可能同时有多个观看者）。</summary>
    private sealed class SessionStream : IAsyncDisposable
    {
        private readonly BrowserScreencastHub _hub;
        private readonly string _sessionId;
        private readonly List<IBrowserFrameSink> _sinks = [];
        private readonly SemaphoreSlim _gate = new(1, 1);

        private ICDPSession? _cdp;
        private BrowserPageHandle? _page;

        /// <summary>本流当前所指的页面（供 DropSessionAsync 比对实例）。</summary>
        internal BrowserPageHandle? PageHandle => _page;
        private FramePump? _pump;
        private CancellationTokenSource? _stopGraceCts;

        /// <summary>
        /// 保护 <see cref="_stopGraceCts"/> 的独立小锁。
        /// </summary>
        /// <remarks>
        /// 不复用 <see cref="_gate"/>：<see cref="StopAfterGraceAsync"/> 已经持 <c>_gate</c> 时
        /// 才需要读写这个字段，若共用会自锁（同一线程重复 WaitAsync 直接死锁）。
        /// </remarks>
        private readonly object _stopGraceGate = new();
        private long _seq;

        /// <summary>
        /// 静帧单飞锁：同一会话同时只允许一张在抓。
        /// </summary>
        /// <remarks>
        /// 用「拿不到就放弃」而不是排队：排队会让一次快速的连续滚动堆出十几张请求，
        /// 前面几张抓回来时画面早就变了（前端按 rev 也会丢弃），纯属浪费 CPU。
        /// </remarks>
        private readonly SemaphoreSlim _stillGate = new(1, 1);

        /// <summary>
        /// 本代次编号（全局递增）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 每次 screencast 启动（首次订阅 / 页面重建 / 视口重启）都换一个新代次。
        /// 前端据此丢弃上一代次的帧：重启后 CDP 的 <c>seq</c> 会从头开始，
        /// 只比 <c>seq</c> 会把新画面全判成旧帧。
        /// </para>
        /// <para>
        /// 用全局递增（而不是每会话从 1 开始）：多会话共用一张前端渲染器时，
        /// 代次必须能跨会话比较。
        /// </para>
        /// </remarks>
        private uint _epoch;

        private static uint s_lastEpoch;

        /// <summary>当前画面代次（无 screencast 时为最后一次的值，从未启动过则 0）。</summary>
        internal uint Epoch => Volatile.Read(ref _epoch);

        private static uint NextEpoch() => Interlocked.Increment(ref s_lastEpoch);

        public SessionStream(BrowserScreencastHub hub, string sessionId)
        {
            _hub = hub;
            _sessionId = sessionId;
        }

        public async Task<IAsyncDisposable> AddViewerAsync(
            BrowserPageHandle page,
            IBrowserFrameSink sink,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // 取消待执行的延迟停止
                CancellationTokenSource? pending;
                lock (_stopGraceGate)
                {
                    pending = _stopGraceCts;
                    _stopGraceCts = null;
                }

                if (pending is not null)
                {
                    await pending.CancelAsync().ConfigureAwait(false);
                    pending.Dispose();
                }

                // 页面换过（重建）或尚未启动：重启 screencast。
                // 必须在 sink 入列**之前**完成：StartCore 失败（页面刚被关、CDP 断开）时异常外抛，
                // 若 sink 已入列就永远留在 _sinks 里 —— 该会话 screencast 再也不会因「无观众」停止，
                // 且每帧都会对一个已断开的 WebSocket 调 SendAsync。
                if (_cdp is null || !ReferenceEquals(_page, page))
                {
                    await StopCoreAsync().ConfigureAwait(false);
                    await StartCoreAsync(page).ConfigureAwait(false);
                }

                lock (_sinks)
                    _sinks.Add(sink);

                // 立刻推一次当前状态，前端状态条不用等第一次变化
                sink.OnState(_hub.CreateState(page));

                return new Subscription(this, sink);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>重启 screencast（视口尺寸变化后调用）。</summary>
        /// <remarks>
        /// 失败时不能只让异常外抛：那时 <c>_cdp</c> 已为 null 而 <c>_sinks</c> 非空，
        /// 流进入「有观众、无采集」的死态——画布永久冻结且没有任何提示，
        /// 用户只能关掉工作台重开。这里回滚并明确告知，前端会显示「画面流已中断」。
        /// </remarks>
        internal async Task RestartAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // StopCoreAsync 会把 _page 置 null，所以必须先取出引用
                var page = _page;
                if (page is null)
                    return;

                await StopCoreAsync().ConfigureAwait(false);

                try
                {
                    await StartCoreAsync(page).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _hub._logger.LogWarning(ex, "重启画面流失败，流已停止：{SessionId}", _sessionId);

                    // 回滚 + 通知：保持 _cdp/_page 为 null，让下次订阅/输入重新建立
                    PublishState(_hub.CreateState(page, running: false));
                    PublishError("画面流已中断，正在重建……");
                    throw;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>向全部观看者推一条错误（逐观看者兜底，不抛）。</summary>
        internal void PublishError(string message)
        {
            IBrowserFrameSink[] sinks;
            lock (_sinks)
                sinks = _sinks.ToArray();

            foreach (var sink in sinks)
            {
                try
                {
                    sink.OnError(message);
                }
                catch (Exception ex)
                {
                    _hub._logger.LogDebug(ex, "推送错误消息失败（忽略该观看者）");
                }
            }
        }

        private async Task StartCoreAsync(BrowserPageHandle page)
        {
            var cdp = await page.Page.CreateCDPSessionAsync().ConfigureAwait(false);
            _cdp = cdp;
            _page = page;

            // 每次启动都换新代次：重启后 CDP 的帧序号从头开始，前端只能靠代次判断
            // 「这批帧属于新会话」，否则整段新画面都会被当成旧帧丢掉。
            Volatile.Write(ref _epoch, NextEpoch());
            _pump = new FramePump(Broadcast, _hub._logger);

            cdp.MessageReceived += OnMessageReceived;

            // 用页面**实际**视口（CSS 像素）作上限。实测 screencast 永远按 CSS 视口出图
            // （与 dsf 无关）：给一个比视口小的值，Chromium 会先等比缩小再编码；给配置值则
            // 面板一拖宽就被降采样。两者都会让文字发虚，见 PixelRatio / FrameScale 的注释。
            await cdp.SendAsync("Page.startScreencast", new Dictionary<string, object>
            {
                ["format"] = "jpeg",
                ["quality"] = JpegQuality,
                ["maxWidth"] = page.ViewportWidth,
                ["maxHeight"] = page.ViewportHeight,
                ["everyNthFrame"] = 1
            }).ConfigureAwait(false);

            _hub._logger.LogDebug(
                "已为会话 {SessionId} 启动画面流（视口 {VW}×{VH}, dpr={Dpr}, frameScale={Scale}）",
                _sessionId, page.ViewportWidth, page.ViewportHeight,
                page.PixelRatio, page.FrameScale);
        }

        private async Task StopCoreAsync()
        {
            if (_cdp is { } cdp)
            {
                cdp.MessageReceived -= OnMessageReceived;

                try
                {
                    await cdp.SendAsync("Page.stopScreencast", new Dictionary<string, object>()).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _hub._logger.LogDebug(ex, "停止画面流失败（忽略）");
                }

                try
                {
                    await cdp.DetachAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 页面可能已关闭
                }
            }

            _cdp = null;
            _page = null;
            _pump = null;
        }

        /// <summary>
        /// 抓一张当前视口静帧（单飞；正在抓就放弃）。
        /// </summary>
        internal async Task<BrowserFrame?> CaptureStillAsync(long rev, CancellationToken cancellationToken)
        {
            var page = _page;
            if (page is null)
                return null;

            bool acquired;

            try
            {
                acquired = await _stillGate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (!acquired)
                return null;

            try
            {
                return await BrowserStillSource
                    .CaptureAsync(page, Epoch, rev, cancellationToken, _hub._logger)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            finally
            {
                _stillGate.Release();
            }
        }

        /// <summary>
        /// 收到一帧 screencast。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>ack 必须恰好一次</b>：Chromium 收到 <c>Page.screencastFrameAck</c> 才推下一帧。
        /// 漏一次的表现不是「掉一帧」，而是画面<b>永久停在最后一帧</b>；多发一次会让
        /// Chromium 的帧窗口计数错乱（后续帧被提前释放或永远不来）。
        /// </para>
        /// <para>
        /// 因此这里把 ack 包装成不可重入的单次动作，并把它的所有权「转移」给
        /// <see cref="FramePump"/>：泵在广播完（或该帧被更新的帧覆盖、被丢弃）时调用它。
        /// 所有提前退出的分支（空帧、没有 sessionId、没有泵）都在 <c>finally</c> 里就地 ack。
        /// </para>
        /// </remarks>
        private void OnMessageReceived(object? sender, MessageEventArgs e)
        {
            if (!string.Equals(e.MessageID, "Page.screencastFrame", StringComparison.Ordinal))
                return;

            var cdp = _cdp;
            if (cdp is null)
                return;

            // null 表示「ack 的所有权已经不在本方法」
            Action? ack = null;

            try
            {
                using var document = JsonDocument.Parse(e.MessageData.ToString() ?? "{}");
                var root = document.RootElement;

                var cdpSessionId = root.TryGetProperty("sessionId", out var sessionElement)
                                   && sessionElement.ValueKind == JsonValueKind.Number
                    ? sessionElement.GetInt32()
                    : 0;

                // 没有 sessionId 就无法回 ack（Chromium 也不认），但仍要走同一条单次出口，
                // 保证「所有分支都恰好经过一次 ack」这条不变量在代码上是统一的。
                ack = cdpSessionId != 0
                    ? AckOnce(() => SendFrameAck(cdp, cdpSessionId))
                    : AckOnce(static () => { });

                var base64 = root.TryGetProperty("data", out var dataElement)
                    ? dataElement.GetString()
                    : null;

                if (string.IsNullOrEmpty(base64))
                    return;     // finally 里 ack

                var metadata = root.TryGetProperty("metadata", out var metadataElement)
                    ? metadataElement
                    : default;

                var width = metadata.ValueKind == JsonValueKind.Object
                            && metadata.TryGetProperty("deviceWidth", out var dw)
                            && dw.ValueKind == JsonValueKind.Number
                    ? (int)dw.GetDouble()
                    : _hub._options.CurrentValue.EffectiveWidth;

                var height = metadata.ValueKind == JsonValueKind.Object
                             && metadata.TryGetProperty("deviceHeight", out var dh)
                             && dh.ValueKind == JsonValueKind.Number
                    ? (int)dh.GetDouble()
                    : _hub._options.CurrentValue.EffectiveHeight;

                var bytes = Convert.FromBase64String(base64);
                var seq = Interlocked.Increment(ref _seq);
                var frame = new BrowserFrame(bytes, seq, width, height, BrowserFrameKind.Flow, Epoch);

                var pump = _pump;
                if (pump is null)
                    return;     // finally 里 ack

                pump.Offer(frame, ack);
                ack = null;     // 所有权转移：泵保证恰好一次
            }
            catch (Exception ex)
            {
                _hub._logger.LogDebug(ex, "解析画面帧失败（忽略该帧）");
            }
            finally
            {
                // 解析失败 / 空帧 / 没有泵：立即 ack，绝不让 Chromium 等一个永远不会来的 ack
                if (ack is not null)
                {
                    try
                    {
                        ack();
                    }
                    catch (Exception ex)
                    {
                        _hub._logger.LogDebug(ex, "画面帧 ack 失败（忽略）");
                    }
                }
            }
        }

        /// <summary>把一个动作包装成「最多执行一次」。</summary>
        private static Action AckOnce(Action ack)
        {
            var done = 0;
            return () =>
            {
                if (Interlocked.Exchange(ref done, 1) == 0)
                    ack();
            };
        }

        /// <summary>回帧应答。失败（CDP 已断开）不抛：ack 是尽力而为，不能连带打断帧回调。</summary>
        private static void SendFrameAck(ICDPSession cdp, int cdpSessionId)
        {
            _ = cdp.SendAsync("Page.screencastFrameAck", new Dictionary<string, object>
            {
                ["sessionId"] = cdpSessionId
            }).ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        /// <summary>向本流的全部观看者推一条状态。</summary>
        internal void PublishState(BrowserPageState state)
        {
            IBrowserFrameSink[] sinks;
            lock (_sinks)
                sinks = _sinks.ToArray();

            foreach (var sink in sinks)
            {
                try
                {
                    sink.OnState(state);
                }
                catch (Exception ex)
                {
                    _hub._logger.LogDebug(ex, "推送页面状态失败（忽略该观看者）");
                }
            }
        }

        internal void Broadcast(BrowserFrame frame)
        {
            IBrowserFrameSink[] sinks;
            lock (_sinks)
                sinks = _sinks.ToArray();

            foreach (var sink in sinks)
            {
                try
                {
                    sink.OnFrame(frame);
                }
                catch (Exception ex)
                {
                    _hub._logger.LogDebug(ex, "推送画面帧失败（忽略该观看者）");
                }
            }
        }

        internal Task RemoveViewerAsync(IBrowserFrameSink sink)
        {
            bool empty;
            lock (_sinks)
            {
                _sinks.Remove(sink);
                empty = _sinks.Count == 0;
            }

            if (!empty)
                return Task.CompletedTask;

            // 最后一个观看者离开：延迟停止（刷新页面会先断后连，立刻停会反复启停 screencast）。
            // 延迟必须是后台任务，不能 await：调用方是订阅的 Dispose，等 1 分钟会让
            // 「关闭标签页/断开连接」看起来像卡死（实测一次端到端用例白等 60 秒）。
            var cts = new CancellationTokenSource();

            // 赋值必须持锁：AddViewerAsync 在锁内取消并置 null，锁外赋值会让二者交错，
            // 产生「cts 指向一个不会被取消的对象、而 _sinks 非空」的窗口。
            lock (_stopGraceGate)
                _stopGraceCts = cts;

            // 显式吞掉未观察异常：这是纯后台任务，异常若逃逸会变成
            // TaskScheduler.UnobservedTaskException（默认行为在某些宿主下会终止进程），
            // 而「延迟停止失败」本身是可以安全忽略的（下次订阅会重建 screencast）。
            _ = StopAfterGraceAsync(cts).ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);

            return Task.CompletedTask;
        }

        private async Task StopAfterGraceAsync(CancellationTokenSource cts)
        {
            try
            {
                await Task.Delay(StopGrace, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // 双检观众：宽限期内可能有人重新连上（刷新完成、另一个标签页打开）。
                // 只看 _stopGraceCts 引用相等是不够的 —— 那条流正被看着却会被停掉，
                // 而前端 WS 仍 Open（不会触发 onclose 重连），画布永久冻结。
                lock (_sinks)
                {
                    if (_sinks.Count > 0)
                        return;
                }

                lock (_stopGraceGate)
                {
                    if (!ReferenceEquals(_stopGraceCts, cts))
                        return;

                    _stopGraceCts = null;
                }

                await StopCoreAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _hub._logger.LogDebug(ex, "延迟停止画面流失败（忽略）");
            }
            finally
            {
                _gate.Release();
                cts.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            CancellationTokenSource? cts;
            lock (_stopGraceGate)
            {
                cts = _stopGraceCts;
                _stopGraceCts = null;
            }

            cts?.Cancel();
            cts?.Dispose();

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
                // 不 Dispose 信号量：后台的延迟停止任务可能在下一拍仍要取锁
                // （Dispose 后取锁会抛 ObjectDisposedException，且该异常发生在无法 observe 的位置）
            }
        }

        private sealed class Subscription : IAsyncDisposable
        {
            private readonly SessionStream _stream;
            private readonly IBrowserFrameSink _sink;
            private bool _disposed;

            public Subscription(SessionStream stream, IBrowserFrameSink sink)
            {
                _stream = stream;
                _sink = sink;
            }

            public async ValueTask DisposeAsync()
            {
                if (_disposed)
                    return;

                _disposed = true;
                await _stream.RemoveViewerAsync(_sink).ConfigureAwait(false);
            }
        }
    }
}

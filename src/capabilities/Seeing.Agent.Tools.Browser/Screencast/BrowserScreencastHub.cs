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
    /// 向该会话的所有观看者广播一次页面状态（操作前后各调一次，前端遮罩才跟着开关）。
    /// </summary>
    /// <remarks>
    /// 没有流（还没人看）时静默返回：这不是错误，只是没人订阅。
    /// </remarks>
    public void PublishState(string sessionId, BrowserPageHandle page)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        if (!_streams.TryGetValue(sessionId, out var stream))
            return;

        // Running 恒为 true：本方法只在「有页面且正在推状态」时被调用
        // （调用方都先确认过 page 存在）。页面关闭走的是 DropSessionAsync 那条路径，
        // 那里推的是 running=false。
        stream.PublishState(new BrowserPageState(
            page.CurrentUrl, page.CurrentTitle, page.RefEpoch, page.IsBusy, Running: true));
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
                sink.OnState(new BrowserPageState(
                    page.CurrentUrl, page.CurrentTitle, page.RefEpoch, page.IsBusy, true));

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
                    PublishState(new BrowserPageState(
                        page.CurrentUrl, page.CurrentTitle, page.RefEpoch, false, false));
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
            _pump = new FramePump(this);

            cdp.MessageReceived += OnMessageReceived;

            // 用「视口 × 像素比」= 真实物理像素。
            // 只用 CSS 视口会在高 DPI 屏上被放大显示（文字发虚）；
            // 用配置值则面板一拖宽就被降采样。两者都不行，见 PixelRatio 的注释。
            await cdp.SendAsync("Page.startScreencast", new Dictionary<string, object>
            {
                ["format"] = "jpeg",
                ["quality"] = JpegQuality,
                ["maxWidth"] = page.RenderWidth,
                ["maxHeight"] = page.RenderHeight,
                ["everyNthFrame"] = 1
            }).ConfigureAwait(false);

            _hub._logger.LogDebug(
                "已为会话 {SessionId} 启动画面流（视口 {VW}×{VH}, 出图 {RW}×{RH}, dpr={Dpr}）",
                _sessionId, page.ViewportWidth, page.ViewportHeight,
                page.RenderWidth, page.RenderHeight, page.PixelRatio);
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

        private void OnMessageReceived(object? sender, MessageEventArgs e)
        {
            if (!string.Equals(e.MessageID, "Page.screencastFrame", StringComparison.Ordinal))
                return;

            var cdp = _cdp;
            if (cdp is null)
                return;

            try
            {
                using var document = JsonDocument.Parse(e.MessageData.ToString() ?? "{}");
                var root = document.RootElement;

                var base64 = root.TryGetProperty("data", out var dataElement)
                    ? dataElement.GetString()
                    : null;

                var sessionId = root.TryGetProperty("sessionId", out var sessionElement)
                                && sessionElement.ValueKind == JsonValueKind.Number
                    ? sessionElement.GetInt32()
                    : 0;

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

                // ack 无条件发出去（无论我们是否用这一帧），否则 Chromium 停止推帧
                if (sessionId != 0)
                {
                    _ = cdp.SendAsync("Page.screencastFrameAck", new Dictionary<string, object>
                    {
                        ["sessionId"] = sessionId
                    }).ContinueWith(
                        static t => _ = t.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default);
                }

                if (string.IsNullOrEmpty(base64))
                    return;

                var bytes = Convert.FromBase64String(base64);
                var seq = Interlocked.Increment(ref _seq);
                _pump?.Offer(new BrowserFrame(bytes, seq, width, height));
            }
            catch (Exception ex)
            {
                _hub._logger.LogDebug(ex, "解析画面帧失败（忽略该帧）");
            }
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

    /// <summary>
    /// 帧泵：只保留最新帧，串行写给观看者。
    /// <para>慢客户端只丢帧、不阻塞 CDP 回调；CDP 回调阻塞会连带卡住浏览器。</para>
    /// </summary>
    private sealed class FramePump
    {
        private readonly SessionStream _stream;
        private readonly object _lock = new();

        private BrowserFrame? _pending;
        private bool _flushing;

        public FramePump(SessionStream stream) => _stream = stream;

        public void Offer(BrowserFrame frame)
        {
            lock (_lock)
            {
                // 覆盖旧帧：宁可丢中间帧，也不要堆积
                _pending = frame;

                if (_flushing)
                    return;

                _flushing = true;
            }

            _ = Task.Run(FlushAsync);
        }

        private async Task FlushAsync()
        {
            while (true)
            {
                BrowserFrame? frame;
                lock (_lock)
                {
                    frame = _pending;
                    _pending = null;

                    if (frame is null)
                    {
                        _flushing = false;
                        return;
                    }
                }

                try
                {
                    _stream.Broadcast(frame);
                }
                catch
                {
                    // Broadcast 内部已逐观看者兜底
                }

                // 让出线程：高帧率下避免占满线程池
                await Task.Yield();
            }
        }
    }
}

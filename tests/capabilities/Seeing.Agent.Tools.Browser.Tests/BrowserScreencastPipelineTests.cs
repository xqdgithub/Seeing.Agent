using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PuppeteerSharp;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 画面流管线：帧泵的 ack 恰好一次、静帧源的尺寸与开关。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么 ack 值得单独测</b>：Chromium 收到 <c>Page.screencastFrameAck</c> 才推下一帧。
/// 漏一次的表现不是「掉一帧」，而是<b>画面永久停在最后一帧</b>；多发一次会让帧窗口计数错乱。
/// 所以「恰好一次」是契约，不是实现细节。
/// </para>
/// <para>
/// 本文件只用 <see cref="FramePump.DrainAsync"/> 这种完成信号同步，不使用固定等待。
/// </para>
/// </remarks>
public class BrowserScreencastPipelineTests
{
    [Fact]
    public async Task FramePump_EveryFrame_ShouldAckExactlyOnce()
    {
        var acks = 0;
        var pump = FramePump.CreateForTest(_ => { });
        pump.Offer(new BrowserFrame([1], 1, 1, 1), () => Interlocked.Increment(ref acks));
        pump.Offer(new BrowserFrame([2], 2, 1, 1), () => Interlocked.Increment(ref acks));
        await pump.DrainAsync();
        acks.Should().Be(2);
    }

    [Fact]
    public async Task FramePump_WhenBroadcastThrows_ShouldStillAck()
    {
        var acks = 0;
        var pump = FramePump.CreateForTest(_ => throw new InvalidOperationException());
        pump.Offer(new BrowserFrame([1], 1, 1, 1), () => Interlocked.Increment(ref acks));
        await pump.DrainAsync();
        acks.Should().Be(1);
    }

    [Fact]
    public async Task FramePump_WhenPinnedInFlush_ShouldAckReplacedFrameExactlyOnce()
    {
        // 目标路径（旧实现在这里必红）：泵正卡在广播 A 帧（_flushing == true），
        // 期间 B 帧入列、又被 C 帧顶掉。B 从未被广播，但它的 ack 必须恰好来一次——
        // 否则那一帧的 Chromium 窗口不释放，后续帧不再到达（画面永久停在最后一帧）。
        //
        // 不用固定等待：用 TCS 把泵钉在 flush 里，场景边界完全由测试控制。
        var broadcastEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBroadcast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var pump = FramePump.CreateForTest(_ =>
        {
            broadcastEntered.TrySetResult();

            // 测试桩：把泵钉在「正在广播」的状态（这是被测场景本身，不是等待技巧）
            releaseBroadcast.Task.GetAwaiter().GetResult();
        });

        var acks = new int[3];

        pump.Offer(new BrowserFrame([1], 1, 1, 1), () => Interlocked.Increment(ref acks[0]));
        await broadcastEntered.Task;        // A 帧已进入广播 → 泵处于 flushing

        pump.Offer(new BrowserFrame([2], 2, 1, 1), () =>
        {
            // B 帧的 ack 会抛（CDP 已断的现场）：仍必须被调用一次，
            // 且不能把泵打断，否则 C 帧永远发不出去。
            Interlocked.Increment(ref acks[1]);
            throw new InvalidOperationException();
        });

        pump.Offer(new BrowserFrame([3], 3, 1, 1), () => Interlocked.Increment(ref acks[2]));

        releaseBroadcast.TrySetResult();
        await pump.DrainAsync();

        acks[0].Should().Be(1, "广播中的帧必须在广播后 ack");
        acks[1].Should().Be(1, "被覆盖的帧必须 ack，且只一次（即使 ack 自身抛异常）");
        acks[2].Should().Be(1, "顶替后的帧必须 ack");
    }

    [Fact]
    public async Task FramePump_WhenNoFrameOffered_ShouldDrainImmediately()
    {
        var pump = FramePump.CreateForTest(_ => { });

        await pump.DrainAsync();
    }

    [Fact]
    public async Task TryCaptureStill_WhenHighResStream_ShouldReturnNull()
    {
        var hub = new BrowserScreencastHub(new TestOptionsMonitor<BrowserOptions>(
            new BrowserOptions { HighResStream = true }));
        (await hub.TryCaptureStillAsync("x", 1, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task TryCaptureStill_WhenNoStream_ShouldReturnNull()
    {
        // 没人看画面（或会话不存在）时没有流，抓静帧既没意义也没页面可用。
        var hub = new BrowserScreencastHub(new TestOptionsMonitor<BrowserOptions>(new BrowserOptions()));

        (await hub.TryCaptureStillAsync("没有这条流", 1, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task TryCaptureStill_WhenBlankSessionId_ShouldReturnNull()
    {
        var hub = new BrowserScreencastHub(new TestOptionsMonitor<BrowserOptions>(new BrowserOptions()));

        (await hub.TryCaptureStillAsync("  ", 1, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public void ResolveSize_ShouldScaleByPixelRatio()
    {
        var page = NewPageHandle(new BrowserOptions { ViewportWidth = 800, ViewportHeight = 600 });

        // 建页时还不知道前端 dpr，默认 1
        var (width, height) = BrowserStillSource.ResolveSize(page);

        width.Should().Be(800);
        height.Should().Be(600);
    }

    [Fact]
    public void ResolveSize_WhenViewportTooLarge_ShouldClampToHeaderLimits()
    {
        // 头里宽高是 u16，协议侧另外限制到 4096；配置写坏不能让帧头溢出。
        var page = NewPageHandle(new BrowserOptions { ViewportWidth = 9999, ViewportHeight = 9999 });

        var (width, height) = BrowserStillSource.ResolveSize(page);

        width.Should().Be(4096);
        height.Should().Be(4096);
    }

    [Fact]
    public void ResolveSize_WhenViewportTooSmall_ShouldClampToMinimum()
    {
        // 下限与 BrowserPageHandle 的视口约束一致：宽 ≥ 320、高 ≥ 240
        var page = NewPageHandle(new BrowserOptions { ViewportWidth = 1, ViewportHeight = 1 });

        var (width, height) = BrowserStillSource.ResolveSize(page);

        width.Should().Be(320);
        height.Should().Be(240);
    }

    [Fact]
    public async Task CaptureAsync_WhenScreenshotThrows_ShouldFallBackToCdp()
    {
        // 主路径（Puppeteer screenshot）失败时必须走 CDP 退路，而不是放弃整张静帧。
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0x01 };
        var session = new Mock<ICDPSession>();

        session.Setup(s => s.SendAsync(
                It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<bool>(), It.IsAny<CommandOptions?>()))
            .ReturnsAsync((string method, object? _, bool __, CommandOptions? ___) => method switch
            {
                "Page.getLayoutMetrics" => Json(
                    """{"cssVisualViewport":{"pageX":0,"pageY":120,"clientWidth":800,"clientHeight":600}}"""),
                "Page.captureScreenshot" => Json(
                    $$"""{"data":"{{Convert.ToBase64String(jpeg)}}"}"""),
                _ => null
            });

        var page = new Mock<IPage>();
        page.Setup(p => p.ScreenshotDataAsync(It.IsAny<ScreenshotOptions>()))
            .ThrowsAsync(new PuppeteerException("主路径截图失败"));
        page.Setup(p => p.CreateCDPSessionAsync()).ReturnsAsync(session.Object);

        var handle = NewPageHandle(new BrowserOptions { ViewportWidth = 800, ViewportHeight = 600 }, page.Object);

        var frame = await BrowserStillSource.CaptureAsync(handle, epoch: 7, rev: 3, CancellationToken.None);

        frame.Should().NotBeNull();
        frame!.Data.Should().Equal(jpeg);
        frame.Kind.Should().Be(BrowserFrameKind.Still);
        frame.Seq.Should().Be(3);           // still 的 seq 承载 rev
        frame.Epoch.Should().Be(7u);

        session.Verify(
            s => s.SendAsync("Page.captureScreenshot", It.IsAny<object?>(), It.IsAny<bool>(), It.IsAny<CommandOptions?>()),
            Times.Once);
        session.Verify(s => s.DetachAsync(), Times.Once);
    }

    [Fact]
    public async Task CaptureAsync_WhenScreenshotHangs_ShouldReturnNullWithinBudget()
    {
        // 截图卡死（页面正在导航、有头窗口被系统挡住）时不能让调用方一起卡住：
        // 800ms 预算到点就放弃，前端 2s 后自会重请求。
        var never = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        var page = new Mock<IPage>();
        page.Setup(p => p.ScreenshotDataAsync(It.IsAny<ScreenshotOptions>())).Returns(never.Task);

        var handle = NewPageHandle(new BrowserOptions(), page.Object);

        var stopwatch = Stopwatch.StartNew();
        var frame = await BrowserStillSource.CaptureAsync(handle, 1, 1, CancellationToken.None);
        stopwatch.Stop();

        frame.Should().BeNull();

        // 这里断言的是「预算生效」这条规格：不能立刻返回 null（那说明根本没等主路径），
        // 也不能一直挂着。上下界都留了余量，不是精度断言。
        stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(600));
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RestartingStream_ShouldIncreaseEpochAndKeepStateAndFrameHeaderInSync()
    {
        // 验收 #15：「流重建（epoch）后画面继续更新」。
        // 只断言「epoch 变了」不够——前端同时拿 state 与帧头做仲裁；
        // 两者若不同源，重启后要么新帧被当成旧帧丢掉，要么画布按错误的代次重置。
        var options = new BrowserOptions();
        var optionsMonitor = new TestOptionsMonitor<BrowserOptions>(options);
        var hub = new BrowserScreencastHub(optionsMonitor);

        var session = new Mock<ICDPSession>();
        var page = new Mock<IPage>();
        page.Setup(p => p.CreateCDPSessionAsync()).ReturnsAsync(session.Object);

        var handle = new BrowserPageHandle(
            "session-epoch",
            page.Object,
            refEpoch: 1,
            optionsMonitor,
            NullLogger<BrowserPageHandle>.Instance);

        var sink = new RecordingSink();
        await using var subscription = await hub.SubscribeAsync(
            "session-epoch", handle, sink, TestContext.Current.CancellationToken);

        var firstEpoch = hub.EpochOf("session-epoch");
        firstEpoch.Should().BeGreaterThan(0, "订阅成功后必须已有代次，前端不能拿到 0");

        sink.LastState!.Epoch.Should().Be(firstEpoch, "订阅时推的 state 必须与当前代次一致");

        RaiseScreencastFrame(session, "AA==");
        var firstFrame = await sink.WaitNextFrameAsync(TimeSpan.FromSeconds(5));
        firstFrame.Epoch.Should().Be(firstEpoch, "帧头必须与 state 同源");
        firstFrame.Seq.Should().Be(1);

        // 重建流（面板 resize / 页面重建走的是同一条路径）。
        await hub.RestartAsync("session-epoch", TestContext.Current.CancellationToken);

        var secondEpoch = hub.EpochOf("session-epoch");
        secondEpoch.Should().BeGreaterThan(firstEpoch, "重建后代次必须严格变大，否则前端会继续按旧代次丢弃新帧");

        // 推一次状态（端点在真实链路上每个发布点都会走 CreateState）。
        hub.PublishState("session-epoch", handle);
        sink.LastState!.Epoch.Should().Be(secondEpoch, "重建后的 state 必须报告新代次");

        RaiseScreencastFrame(session, "AQ==");
        var secondFrame = await sink.WaitNextFrameAsync(TimeSpan.FromSeconds(5));

        // CDP 的帧序号在重启后会从头开始：靠 seq 已经无法区分新旧，
        // 前端唯一能靠的就是代次 —— 因此帧头必须带新代次。
        secondFrame.Epoch.Should().Be(secondEpoch, "重建后的帧头必须带新代次");
        secondFrame.Epoch.Should().Be(sink.LastState!.Epoch, "state 与帧头必须逐字段一致");
    }

    private static void RaiseScreencastFrame(Mock<ICDPSession> session, string base64)
    {
        var json = JsonSerializer.Serialize(new
        {
            sessionId = 1,
            data = base64,
            metadata = new { deviceWidth = 100, deviceHeight = 50 },
        });

        using var document = JsonDocument.Parse(json);

        // MessageEventArgs 的 setter 是 internal（PuppeteerSharp 不公开构造入口），
        // 这里用反射设置：只在测试里模拟 CDP 推送，生产代码不依赖它。
        var args = new MessageEventArgs();
        SetProperty(args, nameof(MessageEventArgs.MessageID), "Page.screencastFrame");
        SetProperty(args, nameof(MessageEventArgs.MessageData), document.RootElement.Clone());

        session.Raise(s => s.MessageReceived += null, args);
    }

    private static void SetProperty(object target, string name, object value)
    {
        var property = target.GetType().GetProperty(name)
                       ?? throw new InvalidOperationException($"PuppeteerSharp 的 {name} 属性不存在");

        var setter = property.GetSetMethod(nonPublic: true)
                     ?? throw new InvalidOperationException($"PuppeteerSharp 的 {name} 不可写");

        setter.Invoke(target, [value]);
    }

    /// <summary>记录状态与帧的观看者；帧用完成信号通知，不做固定等待。</summary>
    /// <remarks>
    /// <b>必须先查积压队列再等</b>：帧泵在 <c>Task.Run</c> 上广播，第一帧可能在测试调用
    /// <see cref="WaitNextFrameAsync"/> 之前就到。若实现成「每次等待换一个新 TCS」，
    /// 已经到达的帧会被丢掉，表现为「明明在推帧却超时」。
    /// </remarks>
    private sealed class RecordingSink : IBrowserFrameSink
    {
        private readonly object _gate = new();
        private readonly Queue<BrowserFrame> _frames = new();
        private TaskCompletionSource<BrowserFrame>? _waiter;

        public BrowserPageState? LastState { get; private set; }

        public void OnFrame(BrowserFrame frame)
        {
            TaskCompletionSource<BrowserFrame>? waiter;
            lock (_gate)
            {
                waiter = _waiter;
                _waiter = null;

                if (waiter is null)
                    _frames.Enqueue(frame);
            }

            waiter?.TrySetResult(frame);
        }

        public void OnState(BrowserPageState state) => LastState = state;

        public void OnError(string message)
        {
        }

        public async Task<BrowserFrame> WaitNextFrameAsync(TimeSpan timeout)
        {
            Task<BrowserFrame> pending;
            lock (_gate)
            {
                if (_frames.Count > 0)
                    return _frames.Dequeue();

                _waiter ??= new TaskCompletionSource<BrowserFrame>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                pending = _waiter.Task;
            }

            return await pending.WaitAsync(timeout).ConfigureAwait(false);
        }
    }

    private static JsonElement? Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();    // Clone 后即使文档释放仍可安全读取
    }

    private static BrowserPageHandle NewPageHandle(BrowserOptions options, IPage? page = null)
    {
        // 尺寸用例只用到 ViewportWidth/Height/PixelRatio 这几个纯字段，页面本身不参与计算。
        var target = page ?? new Mock<IPage>().Object;
        return new BrowserPageHandle(
            "session",
            target,
            1,
            new TestOptionsMonitor<BrowserOptions>(options),
            NullLogger<BrowserPageHandle>.Instance);
    }
}

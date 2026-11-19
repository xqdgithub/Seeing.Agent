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

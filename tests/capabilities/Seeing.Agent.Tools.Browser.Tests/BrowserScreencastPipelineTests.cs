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
    public async Task FramePump_WhenReplacedFrameAckThrows_ShouldNotBreak()
    {
        // 被覆盖的帧必须 ack（否则那一帧的 Chromium 窗口永远不释放），
        // 但 ack 本身失败（CDP 已断开）不能把泵打断——否则后面的帧一个都发不出去。
        var acks = 0;
        var pump = FramePump.CreateForTest(_ => { });

        pump.Offer(new BrowserFrame([1], 1, 1, 1), () => throw new InvalidOperationException());
        pump.Offer(new BrowserFrame([2], 2, 1, 1), () => Interlocked.Increment(ref acks));

        await pump.DrainAsync();

        acks.Should().Be(1);
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

    private static BrowserPageHandle NewPageHandle(BrowserOptions options)
    {
        // 只用到 ViewportWidth/Height/PixelRatio 这几个纯字段，页面本身不参与计算。
        var page = new Mock<IPage>();
        return new BrowserPageHandle(
            "session",
            page.Object,
            1,
            new TestOptionsMonitor<BrowserOptions>(options),
            NullLogger<BrowserPageHandle>.Instance);
    }
}

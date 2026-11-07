using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Seeing.Agent.WebUI.Services;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>
/// 渲染合并器：窗口内合并、尾随执行、异常不外溢、释放语义。
/// <para><b>时间策略</b>：全部用例注入 <see cref="FakeTimeProvider"/> 并手动 <c>Advance</c>，
/// 不等待真实时长。原先每个用例等 120–250ms 纯属空耗。</para>
/// </summary>
public class RenderCoalescerTests
{
    /// <summary>推进虚拟时钟并让定时器回调落地（回调在线程池上执行）。</summary>
    private static async Task AdvanceAsync(FakeTimeProvider clock, TimeSpan delta)
    {
        clock.Advance(delta);
        await Task.Yield();
    }

    [Fact]
    public async Task Request_MultipleWithinWindow_ShouldRenderOnce()
    {
        var renders = 0;
        var clock = new FakeTimeProvider();
        using var c = new RenderCoalescer(
            () => Interlocked.Increment(ref renders),
            TimeSpan.FromMilliseconds(40),
            timeProvider: clock);

        for (var i = 0; i < 10; i++) c.Request();
        await AdvanceAsync(clock, TimeSpan.FromMilliseconds(41));

        renders.Should().Be(1);
        c.RequestCount.Should().Be(10);
    }

    [Fact]
    public async Task Request_AfterWindowElapsed_ShouldRenderAgain()
    {
        var renders = 0;
        var clock = new FakeTimeProvider();
        using var c = new RenderCoalescer(
            () => Interlocked.Increment(ref renders),
            TimeSpan.FromMilliseconds(30),
            timeProvider: clock);

        c.Request();
        await AdvanceAsync(clock, TimeSpan.FromMilliseconds(31));
        c.Request();
        await AdvanceAsync(clock, TimeSpan.FromMilliseconds(31));

        renders.Should().Be(2);
    }

    [Fact]
    public async Task FlushAsync_WithPending_ShouldRenderImmediately()
    {
        var renders = 0;
        using var c = new RenderCoalescer(
            () => Interlocked.Increment(ref renders),
            TimeSpan.FromSeconds(10),
            timeProvider: new FakeTimeProvider());

        c.Request();
        await c.FlushAsync();

        renders.Should().Be(1);
    }

    [Fact]
    public async Task FlushAsync_WithoutPending_ShouldNotRender()
    {
        var renders = 0;
        using var c = new RenderCoalescer(
            () => Interlocked.Increment(ref renders),
            TimeSpan.FromSeconds(10),
            timeProvider: new FakeTimeProvider());

        await c.FlushAsync();

        renders.Should().Be(0);
    }

    /// <summary>Review Focus：Dispose 默认不尾随渲染（组件销毁后渲染无意义且可能抛错）。</summary>
    [Fact]
    public async Task Dispose_WithoutFlushOption_ShouldNotRender()
    {
        var renders = 0;
        var clock = new FakeTimeProvider();
        var c = new RenderCoalescer(
            () => Interlocked.Increment(ref renders),
            TimeSpan.FromSeconds(10),
            timeProvider: clock);

        c.Request();
        c.Dispose();
        await AdvanceAsync(clock, TimeSpan.FromSeconds(11));

        renders.Should().Be(0);
    }

    [Fact]
    public async Task Dispose_WithFlushOption_ShouldRenderTrailing()
    {
        var renders = 0;
        var clock = new FakeTimeProvider();
        var c = new RenderCoalescer(
            () => Interlocked.Increment(ref renders),
            TimeSpan.FromSeconds(10),
            flushOnDispose: true,
            timeProvider: clock);

        c.Request();
        c.Dispose();
        await AdvanceAsync(clock, TimeSpan.FromSeconds(11));

        renders.Should().Be(1);
    }

    [Fact]
    public async Task Request_AfterDispose_ShouldBeIgnored()
    {
        var renders = 0;
        var clock = new FakeTimeProvider();
        var c = new RenderCoalescer(
            () => Interlocked.Increment(ref renders),
            TimeSpan.FromMilliseconds(20),
            timeProvider: clock);

        c.Dispose();
        c.Request();
        await AdvanceAsync(clock, TimeSpan.FromMilliseconds(21));

        renders.Should().Be(0);
    }

    /// <summary>Review Focus：渲染回调抛异常不得逃逸到定时器线程（会终止进程）。</summary>
    [Fact]
    public async Task Request_WhenRenderThrows_ShouldNotCrashProcess()
    {
        var clock = new FakeTimeProvider();
        using var c = new RenderCoalescer(
            () => throw new InvalidOperationException("模拟渲染异常"),
            TimeSpan.FromMilliseconds(20),
            timeProvider: clock);

        c.Request();
        await AdvanceAsync(clock, TimeSpan.FromMilliseconds(21));

        c.RenderCount.Should().Be(1); // 已尝试渲染，异常被吞
    }
}

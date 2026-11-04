using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>渲染合并器：窗口内合并、尾随执行、异常不外溢、释放语义。</summary>
public class RenderCoalescerTests
{
    [Fact]
    public async Task Request_MultipleWithinWindow_ShouldRenderOnce()
    {
        var renders = 0;
        using var c = new RenderCoalescer(() => Interlocked.Increment(ref renders), TimeSpan.FromMilliseconds(40));

        for (var i = 0; i < 10; i++) c.Request();
        await Task.Delay(250);

        renders.Should().Be(1);
        c.RequestCount.Should().Be(10);
    }

    [Fact]
    public async Task Request_AfterWindowElapsed_ShouldRenderAgain()
    {
        var renders = 0;
        using var c = new RenderCoalescer(() => Interlocked.Increment(ref renders), TimeSpan.FromMilliseconds(30));

        c.Request();
        await Task.Delay(150);
        c.Request();
        await Task.Delay(150);

        renders.Should().Be(2);
    }

    [Fact]
    public async Task FlushAsync_WithPending_ShouldRenderImmediately()
    {
        var renders = 0;
        using var c = new RenderCoalescer(() => Interlocked.Increment(ref renders), TimeSpan.FromSeconds(10));

        c.Request();
        await c.FlushAsync();

        renders.Should().Be(1);
    }

    [Fact]
    public async Task FlushAsync_WithoutPending_ShouldNotRender()
    {
        var renders = 0;
        using var c = new RenderCoalescer(() => Interlocked.Increment(ref renders), TimeSpan.FromSeconds(10));

        await c.FlushAsync();

        renders.Should().Be(0);
    }

    /// <summary>Review Focus：Dispose 默认不尾随渲染（组件销毁后渲染无意义且可能抛错）。</summary>
    [Fact]
    public async Task Dispose_WithoutFlushOption_ShouldNotRender()
    {
        var renders = 0;
        var c = new RenderCoalescer(() => Interlocked.Increment(ref renders), TimeSpan.FromSeconds(10));

        c.Request();
        c.Dispose();
        await Task.Delay(120);

        renders.Should().Be(0);
    }

    [Fact]
    public async Task Dispose_WithFlushOption_ShouldRenderTrailing()
    {
        var renders = 0;
        var c = new RenderCoalescer(() => Interlocked.Increment(ref renders), TimeSpan.FromSeconds(10), flushOnDispose: true);

        c.Request();
        c.Dispose();
        await Task.Delay(120);

        renders.Should().Be(1);
    }

    [Fact]
    public async Task Request_AfterDispose_ShouldBeIgnored()
    {
        var renders = 0;
        var c = new RenderCoalescer(() => Interlocked.Increment(ref renders), TimeSpan.FromMilliseconds(20));

        c.Dispose();
        c.Request();
        await Task.Delay(120);

        renders.Should().Be(0);
    }

    /// <summary>Review Focus：渲染回调抛异常不得逃逸到 Timer 线程（会终止进程）。</summary>
    [Fact]
    public async Task Request_WhenRenderThrows_ShouldNotCrashProcess()
    {
        using var c = new RenderCoalescer(
            () => throw new InvalidOperationException("模拟渲染异常"),
            TimeSpan.FromMilliseconds(20));

        c.Request();
        await Task.Delay(150);

        c.RenderCount.Should().Be(1); // 已尝试渲染，异常被吞
    }
}

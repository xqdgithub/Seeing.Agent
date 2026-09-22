using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Seeing.Agent.Tui.Services;

namespace Seeing.Agent.Tui.Tests.Services;

/// <summary>
/// 宿主停止 → 引擎取消的注册/注销契约：SIGTERM/SIGINT 触发 <c>ApplicationStopping</c> 应取消主循环令牌，
/// 且注册在循环退出后释放后不再回调（不重复取消）。
/// </summary>
public sealed class TuiChatEngineLifecycleTests
{
    [Fact]
    public void RegisterStopCancellation_WhenApplicationStopping_ShouldCancelToken()
    {
        using var lifetime = new FakeHostApplicationLifetime();
        using var cts = new CancellationTokenSource();
        using var registration = TuiChatEngine.RegisterStopCancellation(lifetime, cts);

        cts.IsCancellationRequested.Should().BeFalse();

        lifetime.StopApplication();

        cts.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void RegisterStopCancellation_AlreadyStopped_ShouldCancelImmediately()
    {
        using var lifetime = new FakeHostApplicationLifetime();
        lifetime.StopApplication();

        using var cts = new CancellationTokenSource();
        using var registration = TuiChatEngine.RegisterStopCancellation(lifetime, cts);

        cts.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void RegisterStopCancellation_AfterDispose_ShouldNotCancelAgain()
    {
        using var lifetime = new FakeHostApplicationLifetime();
        using var cts = new CancellationTokenSource();
        var registration = TuiChatEngine.RegisterStopCancellation(lifetime, cts);

        // 模拟主循环退出后释放注册。
        registration.Dispose();
        lifetime.StopApplication();

        cts.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public void RegisterStopCancellation_WithoutLifetime_ShouldBeNoOp()
    {
        using var cts = new CancellationTokenSource();

        var registration = TuiChatEngine.RegisterStopCancellation(null, cts);
        registration.Dispose();

        cts.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public void IsCancelArmed_WithinWindowAndExecuting_ShouldBeTrue()
    {
        var now = DateTime.UtcNow;

        TuiChatEngine.IsCancelArmed(now.AddMilliseconds(-500), now, executing: true).Should().BeTrue();
    }

    [Theory]
    [InlineData(null, true, false)]      // 未武装
    [InlineData(-4000, true, false)]     // 超出确认窗口
    [InlineData(-500, false, false)]     // 执行已结束
    public void IsCancelArmed_Otherwise_ShouldBeFalse(int? armedAgoMs, bool executing, bool expected)
    {
        var now = DateTime.UtcNow;
        var armedAt = armedAgoMs is null ? (DateTime?)null : now.AddMilliseconds(armedAgoMs.Value);

        TuiChatEngine.IsCancelArmed(armedAt, now, executing).Should().Be(expected);
    }

    private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _stopping.Cancel();

        public void Dispose() => _stopping.Dispose();
    }
}

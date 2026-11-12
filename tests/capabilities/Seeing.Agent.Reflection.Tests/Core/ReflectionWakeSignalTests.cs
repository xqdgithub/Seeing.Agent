using FluentAssertions;
using Seeing.Agent.Reflection.Core;
using Xunit;

namespace Seeing.Agent.Reflection.Tests.Core;

/// <summary>
/// 手动触发的唤醒信号语义。
/// <para>
/// 它决定「点了按钮之后多久开始跑」：没有信号时 Worker 只能等下一次 1 分钟 tick，
/// 用户点完看不到任何动静。这里钉住三条：未置位时超时返回、置位后立即返回、重复置位不叠加。
/// </para>
/// </summary>
public sealed class ReflectionWakeSignalTests
{
    /// <summary>极短超时：只验证「有无信号」语义，不需要真的等 1 分钟（E5 轮询语义由被测方控制）。</summary>
    private static readonly TimeSpan Tiny = TimeSpan.FromMilliseconds(30);

    [Fact]
    public async Task Wait_Should_Time_Out_When_Not_Pulsed()
    {
        var signal = new ReflectionWakeSignal();

        var woke = await signal.WaitAsync(Tiny, TestContext.Current.CancellationToken);

        woke.Should().BeFalse("没有脉冲时应按超时返回，让 Worker 走正常 tick");
    }

    [Fact]
    public async Task Wait_Should_Return_Immediately_After_Pulse()
    {
        var signal = new ReflectionWakeSignal();
        signal.Pulse();

        (await signal.WaitAsync(Tiny, TestContext.Current.CancellationToken))
            .Should().BeTrue("脉冲必须立刻兑现，否则手动触发要等满一个 tick");
    }

    [Fact]
    public async Task Repeated_Pulses_Should_Not_Stack()
    {
        var signal = new ReflectionWakeSignal();

        // 连续多次 Pulse 不得抛（计数器满会抛 SemaphoreFullException），也不得留下两次唤醒。
        var act = () =>
        {
            signal.Pulse();
            signal.Pulse();
            signal.Pulse();
        };
        act.Should().NotThrow("信号是「有事」而不是「有 N 件事」，重复置位必须无害");

        (await signal.WaitAsync(Tiny, TestContext.Current.CancellationToken)).Should().BeTrue();
        (await signal.WaitAsync(Tiny, TestContext.Current.CancellationToken))
            .Should().BeFalse("多余脉冲不应留下第二次唤醒");
    }

    [Fact]
    public async Task Wait_Should_Throw_When_Cancelled()
    {
        var signal = new ReflectionWakeSignal();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await signal.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

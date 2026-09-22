using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Tui.Services;

namespace Seeing.Agent.Tui.Tests.Services;

/// <summary>
/// 事件源结束后的主循环调度：进入无事件模式（不再重建读取任务），等待集不得含 null，
/// 避免「每轮立即命中已完成任务」引发 100% CPU 忙循环。
/// </summary>
public sealed class TuiChatEngineEventSourceTests
{
    [Fact]
    public void ShouldEnterNoEventMode_NullResult_ShouldBeTrue()
        => TuiChatEngine.ShouldEnterNoEventMode(null).Should().BeTrue();

    [Fact]
    public void ShouldEnterNoEventMode_WithEvent_ShouldBeFalse()
    {
        var evt = new StreamStartEvent { SessionId = "s", LoopId = "l", Step = 0 };

        TuiChatEngine.ShouldEnterNoEventMode(evt).Should().BeFalse();
    }

    [Fact]
    public void BuildWaitSet_EventSourceEnded_ShouldExcludeNullAndEventTask()
    {
        var input = new TaskCompletionSource().Task;
        var tick = new TaskCompletionSource().Task;

        var set = TuiChatEngine.BuildWaitSet(input, eventTask: null, tick);

        set.Should().HaveCount(2);
        set.Should().NotContainNulls();
        set.Should().Contain(input).And.Contain(tick);
    }

    [Fact]
    public void BuildWaitSet_EventSourceAlive_ShouldIncludeAllThree()
    {
        var input = new TaskCompletionSource().Task;
        var @event = new TaskCompletionSource<IMessageEvent?>().Task;
        var tick = new TaskCompletionSource().Task;

        var set = TuiChatEngine.BuildWaitSet(input, @event, tick);

        set.Should().HaveCount(3);
        set.Should().Contain(input).And.Contain(@event).And.Contain(tick);
    }
}

using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Hosting.Execution;
using Xunit;

namespace Seeing.Agent.Tests.App.Execution;

/// <summary>
/// 事件落盘过滤：决定投影到会话后是否立刻 SaveAsync。
/// <para>
/// <c>Running</c> 必须落盘，否则刷新/重连只能从存储回放到 <c>Pending</c>，
/// 真正在执行中的工具会显示成「等待」（用户反馈的实际现象）。
/// </para>
/// </summary>
public class ExecutionPersistEventFilterTests
{
    [Theory]
    [InlineData(ToolCallStatus.Pending)]
    [InlineData(ToolCallStatus.Running)]
    [InlineData(ToolCallStatus.Success)]
    [InlineData(ToolCallStatus.Failed)]
    [InlineData(ToolCallStatus.Rejected)]
    [InlineData(ToolCallStatus.Cancelled)]
    public void ShouldPersistEvent_ToolCallLifecycleStatus_ShouldPersist(ToolCallStatus status)
    {
        var evt = new ToolCallEvent
        {
            SessionId = "s1",
            LoopId = "l1",
            ToolCallId = "c1",
            ToolName = "glob",
            Status = status
        };

        ExecutionRunner.ShouldPersistEvent(evt).Should().BeTrue();
    }

    [Fact]
    public void ShouldPersistEvent_LoopComplete_ShouldPersist()
    {
        var evt = new LoopCompleteEvent { SessionId = "s1", LoopId = "l1", Success = true };

        ExecutionRunner.ShouldPersistEvent(evt).Should().BeTrue();
    }
}

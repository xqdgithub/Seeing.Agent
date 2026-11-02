using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Hosting.Execution;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App.Execution;

/// <summary>
/// <see cref="ChatEventTracker.ApplyEvent"/> 的返回值语义：是否产生了「需要落盘」的状态变化。
/// <para>
/// 用于避免 Running 进度快照（bash 每 100ms 推一次）触发全量会话写放大，
/// 同时保证 pending→running 这类状态迁移一定落盘（刷新后需显示「执行中」）。
/// </para>
/// </summary>
public class ChatEventTrackerPersistChangeTests
{
    private static (SessionData Session, ChatEventTracker Tracker) CreateStartedSession()
    {
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.UserMessage("hi"));
        var tracker = new ChatEventTracker();
        tracker.ApplyEvent(session, new LoopStartEvent { SessionId = session.Id, LoopId = "L1" });
        tracker.ApplyEvent(session, new StreamStartEvent { SessionId = session.Id, LoopId = "L1", Step = 0 });
        return (session, tracker);
    }

    private static ToolCallEvent Progress(string sessionId, ToolCallStatus status, string output = "") => new()
    {
        SessionId = sessionId,
        LoopId = "L1",
        Type = MessageEventType.ToolCallRunning,
        ToolCallId = "c1",
        ToolName = "bash",
        Status = status,
        Output = output
    };

    [Fact]
    public void PendingToRunning_ShouldReportChanged()
    {
        var (session, tracker) = CreateStartedSession();

        tracker.ApplyEvent(session, Progress(session.Id, ToolCallStatus.Pending)).Should().BeTrue();
        tracker.ApplyEvent(session, Progress(session.Id, ToolCallStatus.Running)).Should().BeTrue();
    }

    [Fact]
    public void RepeatedRunningProgress_ShouldNotReportChanged()
    {
        var (session, tracker) = CreateStartedSession();
        tracker.ApplyEvent(session, Progress(session.Id, ToolCallStatus.Running));

        // 同状态重复进度（bash 流式快照）：不产生落盘需求
        tracker.ApplyEvent(session, Progress(session.Id, ToolCallStatus.Running, "line 1")).Should().BeFalse();
        tracker.ApplyEvent(session, Progress(session.Id, ToolCallStatus.Running, "line 2")).Should().BeFalse();
    }

    [Fact]
    public void RunningToSuccess_ShouldReportChanged()
    {
        var (session, tracker) = CreateStartedSession();
        tracker.ApplyEvent(session, Progress(session.Id, ToolCallStatus.Running));

        tracker.ApplyEvent(session, new ToolCallEvent
        {
            SessionId = session.Id,
            LoopId = "L1",
            Type = MessageEventType.ToolCallComplete,
            ToolCallId = "c1",
            ToolName = "bash",
            Status = ToolCallStatus.Success,
            Output = "done"
        }).Should().BeTrue();
    }

    [Fact]
    public void TaskRunningWithNewTaskId_ShouldReportChangedEvenIfStatusUnchanged()
    {
        var (session, tracker) = CreateStartedSession();
        tracker.ApplyEvent(session, new ToolCallEvent
        {
            SessionId = session.Id,
            LoopId = "L1",
            Type = MessageEventType.ToolCallRunning,
            ToolCallId = "t1",
            ToolName = "task",
            Status = ToolCallStatus.Running
        }).Should().BeTrue(); // 新卡片：pending(null) → running

        var withTaskId = new ToolCallEvent
        {
            SessionId = session.Id,
            LoopId = "L1",
            Type = MessageEventType.ToolCallRunning,
            ToolCallId = "t1",
            ToolName = "task",
            Status = ToolCallStatus.Running,
            Metadata = new Dictionary<string, object> { [TaskMetadataKeys.TaskId] = "child-1" }
        };

        // 状态未变但 TaskId 首次回填：仍需落盘，否则刷新后丢失子会话关联
        tracker.ApplyEvent(session, withTaskId).Should().BeTrue();
    }
}

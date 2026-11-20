using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Hosting.Execution;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App.Execution;

/// <summary>
/// <see cref="ToolCallEvent.BackgroundActivityId"/> 的会话投影与复制语义。
/// <para>
/// 后台活动 id 是执行级强类型事实（禁止走 Metadata 字符串约定）：事件 → <see cref="SessionToolCall"/>
/// 投影必须落到字段上，且首次回填要触发落盘（否则刷新后丢失关联）；fork/分享依赖
/// <see cref="SessionToolCall.Clone"/> 与 <see cref="SessionMessage.Clone"/> 逐字段拷贝。
/// </para>
/// </summary>
public class BackgroundActivityIdProjectionTests
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

    private static ToolCallEvent Progress(
        string sessionId,
        ToolCallStatus status = ToolCallStatus.Running,
        string? backgroundActivityId = null) => new()
    {
        SessionId = sessionId,
        LoopId = "L1",
        Type = status == ToolCallStatus.Running
            ? MessageEventType.ToolCallRunning
            : MessageEventType.ToolCallComplete,
        ToolCallId = "c1",
        ToolName = "bash",
        Status = status,
        Output = "started",
        BackgroundActivityId = backgroundActivityId
    };

    private static SessionToolCall FirstToolCall(SessionData session) =>
        session.Messages.Last(m => m.Role == MessageRole.Assistant).ToolCalls!.Single();

    [Fact]
    public void Tracker_ShouldProjectBackgroundActivityId()
    {
        var (session, tracker) = CreateStartedSession();

        // 先落一张无活动 id 的卡片（模拟 Running 进度快照）
        tracker.ApplyEvent(session, Progress(session.Id)).Should().BeTrue();

        // 同状态、且仍未带活动 id：不产生落盘需求
        tracker.ApplyEvent(session, Progress(session.Id)).Should().BeFalse();

        // 状态未变但活动 id 首次回填：必须落盘（刷新后仍能看到「已转后台」）
        tracker.ApplyEvent(session, Progress(session.Id, backgroundActivityId: "act_1")).Should().BeTrue();
        FirstToolCall(session).BackgroundActivityId.Should().Be("act_1");

        // 同值重复事件：不再触发落盘
        tracker.ApplyEvent(session, Progress(session.Id, backgroundActivityId: "act_1")).Should().BeFalse();
        FirstToolCall(session).BackgroundActivityId.Should().Be("act_1");

        // 终态迁移照常落盘，且活动 id 不被后续空值事件清掉
        tracker.ApplyEvent(session, Progress(session.Id, ToolCallStatus.Success)).Should().BeTrue();
        FirstToolCall(session).BackgroundActivityId.Should().Be("act_1");
    }

    [Fact]
    public void Clone_ShouldKeepBackgroundActivityId()
    {
        var original = new SessionToolCall
        {
            Id = "c1",
            Name = "bash",
            Arguments = "{}",
            Status = "running",
            BackgroundActivityId = "act_1"
        };

        original.Clone().BackgroundActivityId.Should().Be("act_1");

        // fork/分享走 SessionMessage.Clone（内部逐个 Clone SessionToolCall）
        var message = SessionMessage.AssistantMessageWithToolCalls(new List<SessionToolCall> { original });
        var cloned = message.Clone();

        cloned.ToolCalls!.Single().BackgroundActivityId.Should().Be("act_1");
    }
}

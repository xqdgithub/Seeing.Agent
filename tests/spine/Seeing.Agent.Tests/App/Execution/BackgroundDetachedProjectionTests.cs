using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Hosting.Execution;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App.Execution;

/// <summary>
/// <see cref="ToolCallEvent.BackgroundDetached"/> 的会话投影与复制语义。
/// <para>
/// 前台正常完成与转后台在事件层完全同形（<c>Origin=ToolForeground</c> + 工具已结束），
/// 只能靠这个强类型标记区分输出源；首次回填必须触发落盘，否则刷新后 UI 又会退回
/// 「活动尾部覆盖完整结果」的错误渲染。
/// </para>
/// </summary>
public class BackgroundDetachedProjectionTests
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
        bool backgroundDetached = false) => new()
    {
        SessionId = sessionId,
        LoopId = "L1",
        Type = status == ToolCallStatus.Running
            ? MessageEventType.ToolCallRunning
            : MessageEventType.ToolCallComplete,
        ToolCallId = "c1",
        ToolName = "bash",
        Status = status,
        Output = "echo a\necho b\necho c",
        BackgroundDetached = backgroundDetached
    };

    private static SessionToolCall FirstToolCall(SessionData session) =>
        session.Messages.Last(m => m.Role == MessageRole.Assistant).ToolCalls!.Single();

    [Fact]
    public void ChatEventTracker_ShouldProjectBackgroundDetached()
    {
        var (session, tracker) = CreateStartedSession();

        // 前台进度快照：未转后台，不需要额外落盘
        tracker.ApplyEvent(session, Progress(session.Id)).Should().BeTrue();
        tracker.ApplyEvent(session, Progress(session.Id)).Should().BeFalse();
        FirstToolCall(session).BackgroundDetached.Should().BeFalse();

        // 状态未变但转后台首次回填：必须落盘
        tracker.ApplyEvent(session, Progress(session.Id, backgroundDetached: true)).Should().BeTrue();
        FirstToolCall(session).BackgroundDetached.Should().BeTrue();

        // 同值重复事件：不再触发落盘
        tracker.ApplyEvent(session, Progress(session.Id, backgroundDetached: true)).Should().BeFalse();

        // 终态迁移照常落盘，且后续 false 不得把它抹回
        tracker.ApplyEvent(session, Progress(session.Id, ToolCallStatus.Success)).Should().BeTrue();
        FirstToolCall(session).BackgroundDetached.Should().BeTrue();
    }

    [Fact]
    public void Clone_ShouldKeepBackgroundDetached()
    {
        var original = new SessionToolCall
        {
            Id = "c1",
            Name = "bash",
            Arguments = "{}",
            Status = "success",
            BackgroundActivityId = "act_1",
            BackgroundDetached = true
        };

        original.Clone().BackgroundDetached.Should().BeTrue();

        // fork/分享走 SessionMessage.Clone（内部逐个 Clone SessionToolCall）
        var message = SessionMessage.AssistantMessageWithToolCalls(new List<SessionToolCall> { original });
        message.Clone().ToolCalls!.Single().BackgroundDetached.Should().BeTrue();
    }
}

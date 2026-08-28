using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Events;
using Seeing.Agent.Execution;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Tests.Services;

public class SessionWindowTimelineSyncTests
{
    private sealed class Harness
    {
        public MessageTimelineStore Timeline { get; } = new();
        public string SessionId { get; } = "s1";
        public int Renders { get; set; }
        public string? AppliedTitle { get; set; }
        public SessionData Session { get; } = SessionData.Create("p1", "general");
        public string? LoopId { get; set; }
        public SessionMessage? StreamingMessage { get; set; }

        public SessionWindowTimelineSync Create()
        {
            Session.Id = SessionId;
            return new SessionWindowTimelineSync(
                Timeline, SessionId,
                () => Renders++,
                t => AppliedTitle = t,
                () => Session.Messages,
                () => StreamingMessage,
                () => LoopId);
        }
    }

    [Fact]
    public void ProcessEvent_StreamDelta_ShouldSyncAssistantAndRequestRender()
    {
        var h = new Harness();
        var sync = h.Create();
        h.Session.AddMessage(SessionMessage.UserMessage("hi"));
        // 模拟服务端 ChatEventTracker 已把流式内容写入 SessionData（UI 只绑定指针刷新）
        var assistant = SessionMessage.AssistantMessage("这是模拟服务端已写入的流式内容");
        assistant.Id = "m1";
        h.Session.AddMessage(assistant);
        h.StreamingMessage = assistant;

        // delta 含换行 → 触发节流强制刷新（阈值归零路径），避免 8 字符节流阈值干扰断言
        sync.ProcessEvent(new StreamDeltaEvent { SessionId = "s1", ContentDelta = "增量内容\n" });

        h.Timeline.Items.Should().Contain(i =>
            i.Turn != null && i.Turn.Messages.Any(m => m.Id == "m1"));
        h.Renders.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ProcessEvent_ExecutionComplete_ShouldCompleteTurnAndReconcile()
    {
        var h = new Harness();
        var sync = h.Create();
        h.Session.AddMessage(SessionMessage.UserMessage("hi"));
        var assistant = SessionMessage.AssistantMessage("done");
        assistant.Id = "m1";
        h.Session.AddMessage(assistant);
        h.StreamingMessage = assistant;

        sync.ProcessEvent(new ExecutionCompleteEvent { SessionId = "s1", ExecutionId = "e1", Status = ExecutionStatus.Completed });

        h.Timeline.Items.Should().Contain(i => i.Turn != null && i.Turn.IsComplete);
    }

    [Fact]
    public void ProcessEvent_SessionTitleChanged_ShouldApplyTitleOnlyForOwnSession()
    {
        var h = new Harness();
        var sync = h.Create();

        sync.ProcessEvent(new SessionTitleChangedEvent { SessionId = "s1", Title = "新标题" });
        sync.ProcessEvent(new SessionTitleChangedEvent { SessionId = "other", Title = "别会话" });

        h.AppliedTitle.Should().Be("新标题");
        h.Session.Messages.Should().BeEmpty(); // 仅标题更新，无消息副作用
    }

    [Fact]
    public void ProcessEvent_OtherSessionEvent_ShouldBeFiltered()
    {
        var h = new Harness();
        var sync = h.Create();

        sync.ProcessEvent(new StreamDeltaEvent { SessionId = "other", ContentDelta = "污染" });

        h.Timeline.Items.Should().BeEmpty();
        h.Renders.Should().Be(0);
    }

    [Fact]
    public void ProcessEvent_CompactionCompleted_ShouldResetTimeline()
    {
        var h = new Harness();
        var sync = h.Create();
        h.Session.AddMessage(SessionMessage.UserMessage("old"));
        h.Session.AddMessage(SessionMessage.AssistantMessage("prev"));

        sync.ProcessEvent(new CompactionCompletedEvent { SessionId = "s1" });

        // 全量重建：消息仍在（来自 Session），Generation 递增
        h.Timeline.Generation.Should().BeGreaterThan(0);
    }
}

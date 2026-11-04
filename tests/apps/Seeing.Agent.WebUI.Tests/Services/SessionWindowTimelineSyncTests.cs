using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.WebUI.Models.Timeline;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>
/// 事件 → 时间线分派的行为锁。
/// <para>
/// 重点锁「全量重建类事件（压缩完成/失败、命令结果）走完一次 <c>ResetFromSession</c> 后
/// 时间线已完全正确」，从而证明紧随其后的 <c>ReconcileAppendFromSession</c> 是冗余全表遍历。
/// </para>
/// </summary>
public class SessionWindowTimelineSyncTests
{
    private static SessionMessage User(string id, string content)
        => new() { Id = id, Role = MessageRole.User, Content = content, SessionId = "s1" };

    private static SessionMessage Assistant(string id, string loopId, string content, int step = 0)
        => new() { Id = id, Role = MessageRole.Assistant, LoopId = loopId, Step = step, Content = content, SessionId = "s1" };

    private static SessionMessage Summary(string id, string content)
        => new() { Id = id, Role = MessageRole.Assistant, Content = content, IsSummary = true, SessionId = "s1" };

    /// <summary>构造被测对象，消息来源由调用方提供的可变列表模拟会话快照。</summary>
    private static (SessionWindowTimelineSync Sync, MessageTimelineStore Store) Create(
        List<SessionMessage> messages)
    {
        var store = new MessageTimelineStore();
        var sync = new SessionWindowTimelineSync(
            store,
            "s1",
            requestRender: () => { },
            applyTitle: _ => { },
            getMessages: () => messages,
            getStreamingMessage: () => null,
            getLoopId: () => null);

        return (sync, store);
    }

    [Fact]
    public void ProcessEvent_SessionUpdated_ShouldRebuildTimeline()
    {
        var messages = new List<SessionMessage> { User("u1", "一") };
        var (sync, store) = Create(messages);
        sync.ProcessEvent(new SessionUpdatedEvent { SessionId = "s1", Session = SessionData.Create() });

        store.Items.Should().ContainSingle();
    }

    /// <summary>
    /// 压缩完成：一次 Reset 后时间线必须已包含摘要条目与压缩前历史，且折叠态正确。
    /// 若本测试通过，说明事件分支里紧随 Reset 的 ReconcileAppendFromSession 是多余的。
    /// </summary>
    [Fact]
    public void ProcessEvent_CompactionCompleted_ShouldProduceCompleteTimelineInOnePass()
    {
        var messages = new List<SessionMessage>
        {
            User("u1", "旧提问"),
            Assistant("a1", "loop1", "旧回答"),
        };
        var (sync, store) = Create(messages);

        // 压缩产物：摘要插在末尾，之前的消息转为已压缩历史
        messages.Add(Summary("sum1", "这是摘要"));

        sync.ProcessEvent(new CompactionCompletedEvent { SessionId = "s1" });

        store.Items.Should().HaveCount(3);
        // 摘要前的两条历史应为折叠态
        store.Items[0].Message!.IsCompacted.Should().BeTrue();
        store.Items[1].Turn!.Messages.Single().IsCompacted.Should().BeTrue();
        // 摘要本身不折叠
        store.Items[2].Kind.Should().Be(TimelineItemKind.Compaction);
        store.Items[2].Message!.IsCompacted.Should().BeFalse();
    }

    /// <summary>压缩失败：同样一次 Reset 后时间线完整。</summary>
    [Fact]
    public void ProcessEvent_CompactionFailed_ShouldProduceCompleteTimelineInOnePass()
    {
        var messages = new List<SessionMessage>
        {
            User("u1", "提问"),
            Assistant("a1", "loop1", "回答"),
        };
        var (sync, store) = Create(messages);

        sync.ProcessEvent(new CompactionFailedEvent { SessionId = "s1", ErrorMessage = "失败" });

        store.Items.Should().HaveCount(2);
        store.Items[0].Message!.Content.Should().Be("提问");
    }

    /// <summary>命令结果需刷新：一次 Reset 后时间线完整。</summary>
    [Fact]
    public void ProcessEvent_CommandResultNeedsRefresh_ShouldProduceCompleteTimelineInOnePass()
    {
        var messages = new List<SessionMessage> { User("u1", "一"), User("u2", "二") };
        var (sync, store) = Create(messages);

        sync.ProcessEvent(new CommandResultEvent { SessionId = "s1", CommandName = "clear", NeedsRefresh = true });

        store.Items.Should().HaveCount(2);
    }

    /// <summary>
    /// 压缩事件重复投递（回放场景）不应让时间线膨胀。
    /// 这条同时守住「去掉冗余 Reconcile 后单飞不丢条目」。
    /// </summary>
    [Fact]
    public void ProcessEvent_CompactionCompletedTwice_ShouldNotDuplicate()
    {
        var messages = new List<SessionMessage>
        {
            User("u1", "旧提问"),
            Assistant("a1", "loop1", "旧回答"),
            Summary("sum1", "摘要"),
        };
        var (sync, store) = Create(messages);

        sync.ProcessEvent(new CompactionCompletedEvent { SessionId = "s1" });
        sync.ProcessEvent(new CompactionCompletedEvent { SessionId = "s1" });

        store.Items.Should().HaveCount(3);
    }
}

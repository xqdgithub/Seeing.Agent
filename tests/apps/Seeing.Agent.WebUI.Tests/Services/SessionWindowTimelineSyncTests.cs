using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.WebUI.Models.Timeline;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;

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
        List<SessionMessage> messages,
        Func<SessionMessage?>? getStreamingMessage = null,
        Func<string?>? getLoopId = null)
    {
        var store = new MessageTimelineStore();
        var sync = new SessionWindowTimelineSync(
            store,
            "s1",
            requestRender: () => { },
            applyTitle: _ => { },
            getMessages: () => messages,
            getStreamingMessage: getStreamingMessage ?? (() => null),
            getLoopId: getLoopId ?? (() => null));

        return (sync, store);
    }

    /// <summary>
    /// 回归：新一轮执行开始时，上一轮**已完成**的助手消息不得被重新标记为未完成。
    /// <para>
    /// 现实路径：<c>_handler.GetCurrentAssistantMessage()</c> 在新回合的首个 delta 前仍指向
    /// 上一轮最后一条 assistant，<c>_sync</c> 以 <c>isComplete: false</c> 同步它，
    /// 该消息的 <c>IsComplete</c> 被改回 false → <c>RenderMarkdownPolicy</c> 判定为流式 →
    /// Markdown 退化为纯文本（用户可见「发新消息后旧回答的排版失效」）。
    /// </para>
    /// </summary>
    [Fact]
    public void ProcessEvent_ExecutionStarted_ShouldNotDowngradePreviousCompletedTurn()
    {
        var old = Assistant("a1", "loop1", "**旧回答**");
        var messages = new List<SessionMessage> { User("u1", "旧提问"), old };
        var (sync, store) = Create(messages, getStreamingMessage: () => old);

        // 上一轮已正常结束
        sync.ProcessEvent(new ExecutionCompleteEvent
        {
            SessionId = "s1",
            ExecutionId = "e1",
            Outcome = new Seeing.Agent.Abstractions.Execution.ExecutionOutcome
            {
                SessionId = "s1",
                ExecutionId = "e1",
                Status = Seeing.Agent.Abstractions.Execution.ExecutionStatus.Completed
            }
        });

        store.Items.Single(i => i.Key == "loop1").Turn!.Messages.Single().IsComplete.Should().BeTrue();

        // 用户发新消息 → 新一轮执行开始
        sync.ProcessEvent(new ExecutionStartedEvent { SessionId = "s1", ExecutionId = "e2" });

        store.Items.Single(i => i.Key == "loop1").Turn!.Messages.Single().IsComplete
            .Should().BeTrue("上一轮已完成的回答不得因新回合开始而被降级为流式");
    }

    /// <summary>
    /// 回归：真正的流式消息仍必须被标记为未完成（不得因修复上一轮而降级新消息）。
    /// <para>
    /// 真实序列：ExecutionStarted → LoopStart（清空指针）→ StreamStart → delta 时绑定新消息。
    /// </para>
    /// </summary>
    [Fact]
    public void ProcessEvent_StreamStart_ShouldMarkNewStreamingMessageIncomplete()
    {
        var old = Assistant("a1", "loop1", "旧回答");
        var current = Assistant("a2", "loop2", "新回答");
        var messages = new List<SessionMessage> { User("u1", "旧提问"), old, User("u2", "新提问"), current };
        var (sync, store) = Create(messages, getStreamingMessage: () => current);

        sync.ProcessEvent(new ExecutionStartedEvent { SessionId = "s1", ExecutionId = "e2" });
        sync.ProcessEvent(new LoopStartEvent { SessionId = "s1", LoopId = "loop2" });
        sync.ProcessEvent(new StreamStartEvent { SessionId = "s1", LoopId = "loop2", Step = 0 });

        var turn = store.Items.Single(i => i.Turn != null && i.Turn.Messages.Any(m => m.Id == "a2")).Turn!;
        turn.Messages.Single(m => m.Id == "a2").IsComplete
            .Should().BeFalse("新回合正在流式输出");

        // 上一轮仍保持已完成
        store.Items.Single(i => i.Key == "loop1").Turn!.Messages.Single().IsComplete.Should().BeTrue();
    }

    /// <summary>
    /// 回归（2026-11-07）：**非流式事件**不得把已完成的回答降级为流式。
    /// <para>
    /// 现实路径：上一轮 LoopComplete 已把回答标为完成。用户发新消息后，
    /// <c>InboxNoticeEvent</c>/<c>BudgetStatusEvent</c> 等事件落在 <c>ProcessEvent</c> 的
    /// <c>default</c> 分支 → <c>SyncAssistant(isComplete: false)</c>。
    /// 此刻 handler 的 <c>_currentLoopId</c> 仍是上一轮的（LoopStart 尚未到达），
    /// 与消息 LoopId 比对**必然相等**，11-05 加的门闸形同虚设。
    /// 结果：<c>IsComplete</c> 被改回 false → Markdown 退回纯文本，直到刷新页面。
    /// </para>
    /// </summary>
    [Fact]
    public void ProcessEvent_NonStreamingEvent_ShouldNotDowngradeCompletedTurn()
    {
        var old = Assistant("a1", "loop1", "**旧回答**");
        var messages = new List<SessionMessage> { User("u1", "旧提问"), old };

        // handler 指针仍停在上轮（新 LoopStart 尚未到达）——这正是真实窗口
        var (sync, store) = Create(messages, getStreamingMessage: () => old, getLoopId: () => "loop1");

        sync.ProcessEvent(new LoopCompleteEvent { SessionId = "s1", LoopId = "loop1", Success = true });
        store.Items.Single(i => i.Key == "loop1").Turn!.Messages.Single().IsComplete.Should().BeTrue();

        // 用户发出新消息 → 投递通知（default 分支，非流式）
        sync.ProcessEvent(new InboxNoticeEvent { SessionId = "s1", SenderKind = "human" });

        store.Items.Single(i => i.Key == "loop1").Turn!.Messages.Single().IsComplete
            .Should().BeTrue("非流式事件不得把已完成的回答降级为流式（否则 Markdown 塌成纯文本）");
    }

    /// <summary>
    /// 同一条降级路径的第二个入口：Skill 展开事件同样是 default 分支的非流式事件。
    /// </summary>
    [Fact]
    public void ProcessEvent_SkillContent_ShouldNotDowngradeCompletedTurn()
    {
        var old = Assistant("a1", "loop1", "旧回答");
        var messages = new List<SessionMessage> { User("u1", "旧提问"), old };
        var (sync, store) = Create(messages, getStreamingMessage: () => old, getLoopId: () => "loop1");

        sync.ProcessEvent(new LoopCompleteEvent { SessionId = "s1", LoopId = "loop1", Success = true });

        sync.ProcessEvent(new SkillContentEvent
        {
            SessionId = "s1",
            OriginalContent = "/demo",
            ExpandedContent = "展开内容"
        });

        store.Items.Single(i => i.Key == "loop1").Turn!.Messages.Single().IsComplete.Should().BeTrue();
    }

    /// <summary>
    /// 反向守门：真正的流式事件仍必须把**当前回合**的消息标为未完成。
    /// 修复不得把「已完成」latch 成永不回退（同回合内 step 切换需要能回到未完成）。
    /// </summary>
    [Fact]
    public void ProcessEvent_StreamStart_InSameLoop_ShouldStillMarkIncomplete()
    {
        var current = Assistant("a2", "loop2", "新回答");
        var messages = new List<SessionMessage> { User("u1", "旧提问"), current };
        var (sync, store) = Create(messages, getStreamingMessage: () => current, getLoopId: () => "loop2");

        sync.ProcessEvent(new LoopStartEvent { SessionId = "s1", LoopId = "loop2" });
        sync.ProcessEvent(new StreamStartEvent { SessionId = "s1", LoopId = "loop2", Step = 0 });

        store.Items.Single(i => i.Key == "loop2").Turn!.Messages.Single().IsComplete
            .Should().BeFalse("当前回合正在流式输出");
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

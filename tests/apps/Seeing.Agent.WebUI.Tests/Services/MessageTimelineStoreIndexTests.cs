using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>时间线索引化行为锁：同步、认领、幂等、折叠态均不得回归。</summary>
public class MessageTimelineStoreIndexTests
{
    private static SessionMessage Assistant(string id, string? loopId, string content, int step = 0)
        => new() { Id = id, Role = MessageRole.Assistant, LoopId = loopId, Step = step, Content = content, SessionId = "s1" };

    private static SessionMessage User(string id, string content)
        => new() { Id = id, Role = MessageRole.User, Content = content, SessionId = "s1" };

    [Fact]
    public void SyncAssistantMessage_NewLoop_ShouldCreateTurn()
    {
        var store = new MessageTimelineStore();
        store.SyncAssistantMessage(Assistant("a1", "loop1", "你好"), "s1");

        store.Items.Should().ContainSingle()
            .Which.Key.Should().Be("loop1");
        store.Items[0].Turn!.Messages.Should().ContainSingle();
    }

    [Fact]
    public void SyncAssistantMessage_SameLoopMoreSteps_ShouldAppendToSameTurn()
    {
        var store = new MessageTimelineStore();
        store.SyncAssistantMessage(Assistant("a1", "loop1", "一", 0), "s1");
        store.SyncAssistantMessage(Assistant("a2", "loop1", "二", 1), "s1");

        store.Items.Should().ContainSingle();
        store.Items[0].Turn!.Messages.Should().HaveCount(2);
    }

    [Fact]
    public void SyncAssistantMessage_OrphanThenLoopId_ShouldMergeIntoSingleTurn()
    {
        var store = new MessageTimelineStore();
        store.SyncAssistantMessage(Assistant("a1", null, "流式早期"), "s1");
        store.Items[0].Key.Should().StartWith("single-");

        store.SyncAssistantMessage(Assistant("a1", "loop1", "流式早期"), "s1");

        store.Items.Should().ContainSingle();
        store.Items[0].Key.Should().Be("loop1");
        store.Items[0].Turn!.LoopIndex.Should().Be(1);
    }

    [Fact]
    public void ResetFromSession_DuplicateId_ShouldKeepLast()
    {
        var store = new MessageTimelineStore();
        store.ResetFromSession([User("m1", "旧"), User("m1", "新")], "s1");

        store.Items.Should().ContainSingle();
        store.Items[0].Message!.Content.Should().Be("新");
    }

    [Fact]
    public void ReconcileAppendFromSession_ShouldBeIdempotent()
    {
        var store = new MessageTimelineStore();
        var messages = new List<SessionMessage> { User("m1", "一") };
        store.ResetFromSession(messages, "s1");

        messages.Add(User("m2", "二"));
        store.ReconcileAppendFromSession(messages, "s1");
        store.ReconcileAppendFromSession(messages, "s1");

        store.Items.Should().HaveCount(2);
    }

    [Fact]
    public void ReconcileAppendFromSession_ManyCallsSameCollection_ShouldNotGrow()
    {
        var store = new MessageTimelineStore();
        var messages = Enumerable.Range(0, 200).Select(i => User($"m{i}", $"内容{i}")).ToList();
        store.ResetFromSession(messages, "s1");

        for (var i = 0; i < 20; i++)
            store.ReconcileAppendFromSession(messages, "s1");

        store.Items.Should().HaveCount(200);
    }

    [Fact]
    public void SyncAssistantMessage_Unchanged_ShouldNotRaiseChanged()
    {
        var store = new MessageTimelineStore();
        store.SyncAssistantMessage(Assistant("a1", "loop1", "内容"), "s1");

        var raised = 0;
        store.Changed += () => raised++;
        store.SyncAssistantMessage(Assistant("a1", "loop1", "内容"), "s1");

        raised.Should().Be(0);
    }

    /// <summary>Review Focus：认领后索引不得与列表失步，否则消息会被重复追加。</summary>
    [Fact]
    public void AfterOrphanClaim_ReconcileShouldNotDuplicate()
    {
        var store = new MessageTimelineStore();
        var messages = new List<SessionMessage> { User("u1", "提问"), Assistant("a1", null, "早期") };
        store.ResetFromSession(messages, "s1");

        store.SyncAssistantMessage(Assistant("a1", "loop1", "早期"), "s1");
        messages[1] = Assistant("a1", "loop1", "早期");

        store.ReconcileAppendFromSession(messages, "s1");
        store.ReconcileAppendFromSession(messages, "s1");

        store.Items.Should().HaveCount(2);
        store.Items[0].Message!.Id.Should().Be("u1");
        store.Items[1].Key.Should().Be("loop1");
    }

    /// <summary>
    /// Review Focus 守门：UI 层不得回写执行期消息字段（docs/architecture/04 §5.2）。
    /// ResetFromSession 遇到空 Id 消息时只能跳过 + 记录，不得修改 SessionData 的对象。
    /// </summary>
    [Fact]
    public void ResetFromSession_WithEmptyIdMessage_ShouldNotMutateSessionMessage()
    {
        var store = new MessageTimelineStore();
        var legacy = new SessionMessage { Id = "", Role = MessageRole.User, Content = "无 Id" };

        store.ResetFromSession([legacy], "s1");

        legacy.Id.Should().BeEmpty("UI 投影层禁止回写执行期消息字段");
        store.Items.Should().BeEmpty("缺少 Id 的消息无法稳定标识，不进入时间线");
    }

    /// <summary>
    /// 回归（2026-11-07）：`SyncAssistantMessage` 不得提供「默认流式」的语义。
    /// <para>
    /// 历史缺陷：默认参数 <c>isComplete = false</c> 让 <c>SessionWindow.OnAggregatorAssistantChanged</c>
    /// （子 Agent 每上报一个 TaskStep）把父会话里**已完成**的回答重新标成未完成，
    /// 绕过 <c>SessionWindowTimelineSync</c> 的全部门闸，Markdown 塌成纯文本。
    /// </para>
    /// <para>不变量：同步一条**已存在**的消息时，未显式声明完成态就沿用其既有完成态。</para>
    /// </summary>
    [Fact]
    public void SyncAssistantMessage_ExistingCompletedMessage_ShouldNotDowngradeByDefault()
    {
        var store = new MessageTimelineStore();
        store.SyncAssistantMessage(Assistant("a1", "loop1", "**旧回答**"), "s1", isComplete: true);
        store.Items[0].Turn!.Messages.Single().IsComplete.Should().BeTrue();

        // 模拟聚合器回调：只带内容刷新，不声明完成态
        store.SyncAssistantMessage(Assistant("a1", "loop1", "**旧回答**"), "s1");

        store.Items[0].Turn!.Messages.Single().IsComplete
            .Should().BeTrue("未显式声明流式的同步不得把已完成消息降级（否则 Markdown 塌成纯文本）");
    }

    /// <summary>
    /// 反向守门：未声明完成态的同步遇到**首次出现**的消息时，必须按未完成渲染。
    /// <para>否则事件迟到流式消息会被误判为「已完成」，用户看不到流式输出。</para>
    /// </summary>
    [Fact]
    public void SyncAssistantMessage_NewMessageWithoutHint_ShouldRenderAsStreaming()
    {
        var store = new MessageTimelineStore();

        store.SyncAssistantMessage(Assistant("a1", "loop1", "正在输出"), "s1");

        store.Items[0].Turn!.Messages.Single().IsComplete
            .Should().BeFalse("尚未投影过的消息在无完成态声明时按流式处理");
    }

    /// <summary>ReconcileAppendFromSession 同样不得回写 Id。</summary>
    [Fact]
    public void ReconcileAppendFromSession_WithEmptyIdMessage_ShouldNotMutateSessionMessage()
    {
        var store = new MessageTimelineStore();
        store.ResetFromSession(Array.Empty<SessionMessage>(), "s1");
        var legacy = new SessionMessage { Id = "", Role = MessageRole.User, Content = "无 Id" };

        store.ReconcileAppendFromSession([legacy], "s1");

        legacy.Id.Should().BeEmpty("UI 投影层禁止回写执行期消息字段");
        store.Items.Should().BeEmpty();
    }

    /// <summary>正常消息（有 Id）不受影响。</summary>
    [Fact]
    public void ResetFromSession_WithIdMessage_ShouldRenderNormally()
    {
        var store = new MessageTimelineStore();
        var msg = new SessionMessage { Id = "m1", Role = MessageRole.User, Content = "有 Id" };

        store.ResetFromSession([msg], "s1");

        store.Items.Should().ContainSingle();
        store.Items[0].Message!.Id.Should().Be("m1");
    }
}

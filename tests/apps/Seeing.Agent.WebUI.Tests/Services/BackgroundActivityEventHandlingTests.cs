using System.Collections.Immutable;
using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.WebUI.Models.Timeline;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>
/// 后台活动事件（<c>activity.*</c>）→ UI 卡片的投影行为锁。
/// <para>
/// 事件不落盘，UI 侧唯一投影是 <see cref="EventStreamHandler"/> 的活动字典 + 时间线附着规则；
/// 这里同时锁「按发起工具调用内联」「旧 Seq 不回退」「找不到工具时追加独立条目」三条契约。
/// </para>
/// </summary>
public class BackgroundActivityEventHandlingTests
{
    private const string SessionId = "s1";

    private static EventStreamHandler CreateHandler()
    {
        var manager = new Mock<ISessionManager>();
        return new EventStreamHandler(SessionId, manager.Object);
    }

    private static BackgroundActivitySnapshot Snapshot(
        long seq,
        BackgroundActivityStatus status,
        string? originToolCallId,
        string activityId = "act1")
        => new()
        {
            Id = activityId,
            Kind = "process",
            Title = "后台命令",
            OriginToolCallId = originToolCallId,
            OwnerSessionId = SessionId,
            OwnerExecutionId = "e1",
            WorkspaceRoot = ".",
            HostInstanceId = "host",
            Status = status,
            Seq = seq,
            Capabilities = BackgroundActivityCapabilities.CanCancel
        };

    private static BackgroundActivityEvent Event(
        string type,
        BackgroundActivitySnapshot snapshot)
        => new()
        {
            SessionId = SessionId,
            Type = type,
            Activity = snapshot
        };

    /// <summary>构造含发起工具调用的会话消息，并建出时间线（模拟会话首屏）。</summary>
    private static MessageTimelineStore CreateTimelineWithToolCall(string toolCallId, string? activityId = null)
    {
        var message = new SessionMessage
        {
            Id = "a1",
            SessionId = SessionId,
            LoopId = "loop1",
            Role = MessageRole.Assistant,
            Content = "执行中",
            ToolCalls = ImmutableList.Create(new SessionToolCall
            {
                Id = toolCallId,
                Name = "bash",
                Status = "running",
                BackgroundActivityId = activityId
            })
        };

        var store = new MessageTimelineStore();
        store.ResetFromSession(new[] { message }, SessionId);
        return store;
    }

    [Fact]
    public async Task ActivityCompleted_ShouldUpdateCard_ByOriginToolCallId()
    {
        var handler = CreateHandler();
        var timeline = CreateTimelineWithToolCall("tc1");
        var itemCountBefore = timeline.Items.Count;

        await handler.ProcessEventAsync(Event(
            ActivityEventTypes.Completed,
            Snapshot(2, BackgroundActivityStatus.Succeeded, "tc1")));

        handler.TryGetBackgroundActivity("act1", out var activity).Should().BeTrue();

        var changed = BackgroundActivityProjection.Apply(timeline, activity);

        changed.Should().BeTrue();
        timeline.Items.Should().HaveCount(itemCountBefore, "活动应内联进发起工具卡片，而不是新增独立条目");

        var toolCall = timeline.Items
            .SelectMany(i => i.Turn?.Messages ?? Enumerable.Empty<Seeing.Agent.WebUI.Models.MessageViewModel>())
            .SelectMany(m => m.ToolCalls)
            .Single(tc => tc.Id == "tc1");

        toolCall.BackgroundActivity.Should().NotBeNull();
        toolCall.BackgroundActivity!.Id.Should().Be("act1");
        toolCall.BackgroundActivity.Status.Should().Be(BackgroundActivityStatus.Succeeded);
    }

    [Fact]
    public async Task OlderSeq_ShouldNotOverwriteNewer()
    {
        var handler = CreateHandler();

        await handler.ProcessEventAsync(Event(
            ActivityEventTypes.Completed,
            Snapshot(5, BackgroundActivityStatus.Succeeded, "tc1")));

        // 乱序/回放的旧快照：状态仍是 Running，但 Seq 更小
        await handler.ProcessEventAsync(Event(
            ActivityEventTypes.Progress,
            Snapshot(3, BackgroundActivityStatus.Running, "tc1")));

        handler.TryGetBackgroundActivity("act1", out var activity).Should().BeTrue();
        activity.Seq.Should().Be(5);
        activity.Status.Should().Be(BackgroundActivityStatus.Succeeded);
    }

    [Fact]
    public async Task UnknownOrigin_ShouldAppendEntry()
    {
        var handler = CreateHandler();
        var timeline = new MessageTimelineStore();
        timeline.ResetFromSession(Array.Empty<SessionMessage>(), SessionId);

        await handler.ProcessEventAsync(Event(
            ActivityEventTypes.Started,
            Snapshot(1, BackgroundActivityStatus.Running, "missing-tool-call")));

        handler.TryGetBackgroundActivity("act1", out var activity).Should().BeTrue();

        var changed = BackgroundActivityProjection.Apply(timeline, activity);

        changed.Should().BeTrue();
        timeline.Items.Should().ContainSingle();
        timeline.Items[0].Kind.Should().Be(TimelineItemKind.BackgroundActivity);
        timeline.Items[0].Activity.Should().NotBeNull();
        timeline.Items[0].Activity!.Id.Should().Be("act1");
    }

    [Fact]
    public async Task ProgressWithoutKnownActivity_ShouldNotAppendHistory()
    {
        var handler = CreateHandler();
        var timeline = new MessageTimelineStore();
        timeline.ResetFromSession(Array.Empty<SessionMessage>(), SessionId);

        await handler.ProcessEventAsync(Event(
            ActivityEventTypes.Progress,
            Snapshot(1, BackgroundActivityStatus.Running, "missing-tool-call")));

        handler.TryGetBackgroundActivity("act1", out var activity).Should().BeTrue();

        // progress 只更新状态：不允许凭空新增条目
        BackgroundActivityProjection.Apply(timeline, activity, allowAppend: false).Should().BeFalse();
        timeline.Items.Should().BeEmpty();
    }
}

using FluentAssertions;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Models.Timeline;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;
using System.Collections.Immutable;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Models;

/// <summary>版本语义：结构变更与内容变更分别递增；VM 自身内容变更需单独 Touch。</summary>
public class TimelineRevisionTests
{
    [Fact]
    public void Touch_ShouldNotBumpStructureRevision()
    {
        var item = new TimelineItem { Key = "k", Kind = TimelineItemKind.AssistantTurn };
        item.Touch();

        item.Revision.Should().Be(1);
        item.StructureRevision.Should().Be(0);
    }

    [Fact]
    public void TouchStructure_ShouldBumpBoth()
    {
        var item = new TimelineItem { Key = "k", Kind = TimelineItemKind.AssistantTurn };
        item.TouchStructure();

        item.Revision.Should().Be(1);
        item.StructureRevision.Should().Be(1);
    }

    [Fact]
    public void MessageViewModel_Touch_ShouldBumpRevision()
    {
        var vm = new MessageViewModel { Id = "m1" };
        vm.Touch();

        vm.Revision.Should().Be(1);
    }

    /// <summary>
    /// Review Focus 守门：CompleteTurn 后每条消息的 Revision 必须递增，
    /// 否则 LoopGroupItem 的分段缓存不会重建，流式纯文本不会被替换为 Markdown。
    /// </summary>
    [Fact]
    public void CompleteTurn_ShouldBumpEveryMessageRevision()
    {
        var store = new MessageTimelineStore();
        var msg = new SessionMessage
        {
            Id = "a1", Role = MessageRole.Assistant, LoopId = "loop1", Content = "内容", SessionId = "s1"
        };
        store.SyncAssistantMessage(msg, "s1");

        var before = store.Items[0].Turn!.Messages.Single().Revision;
        store.CompleteTurn("loop1");
        var after = store.Items[0].Turn!.Messages.Single().Revision;

        after.Should().BeGreaterThan(before);
        store.Items[0].Turn!.Messages.Single().IsComplete.Should().BeTrue();
    }

    /// <summary>
    /// Review Focus 守门：仅 IsReasoningComplete 变化也必须递增 Revision，
    /// 否则思考块的流式纯文本不会被替换为 Markdown（分段缓存按 VM.Revision 判定）。
    /// </summary>
    [Fact]
    public void CompleteTurn_WhenOnlyReasoningCompletenessChanges_ShouldBumpRevision()
    {
        var store = new MessageTimelineStore();
        var msg = new SessionMessage
        {
            Id = "a1", Role = MessageRole.Assistant, LoopId = "loop1",
            Content = "内容", ReasoningContent = "思考", SessionId = "s1"
        };
        store.SyncAssistantMessage(msg, "s1", isComplete: true);
        // 手工模拟「已完成但思考未标完成」的中间态
        store.Items[0].Turn!.Messages.Single().IsReasoningComplete = false;

        var before = store.Items[0].Turn!.Messages.Single().Revision;
        store.CompleteTurn("loop1");
        var after = store.Items[0].Turn!.Messages.Single().Revision;

        store.Items[0].Turn!.Messages.Single().IsReasoningComplete.Should().BeTrue();
        after.Should().BeGreaterThan(before);
    }

    /// <summary>
    /// Review Focus 守门：工具卡片的耗时/描述变化必须触发 VM.Touch，
    /// 否则分段缓存命中旧片段，工具耗时永久停留旧值。
    /// </summary>
    [Fact]
    public void SyncAssistantMessage_WhenOnlyToolDurationChanges_ShouldBumpRevision()
    {
        var store = new MessageTimelineStore();
        SessionMessage Build(double durationMs) => new()
        {
            Id = "a1", Role = MessageRole.Assistant, LoopId = "loop1", Content = "内容", SessionId = "s1",
            ToolCalls = ImmutableList.Create(new SessionToolCall
            {
                Id = "t1", Name = "bash", Status = "success", DurationMs = durationMs
            })
        };

        store.SyncAssistantMessage(Build(100), "s1");
        var before = store.Items[0].Turn!.Messages.Single().Revision;

        store.SyncAssistantMessage(Build(250), "s1");
        var after = store.Items[0].Turn!.Messages.Single().Revision;

        after.Should().BeGreaterThan(before);
        store.Items[0].Turn!.Messages.Single().ToolCalls.Single().DurationMs.Should().Be(250);
    }
}

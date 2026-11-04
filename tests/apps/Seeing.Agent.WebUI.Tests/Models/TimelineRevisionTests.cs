using FluentAssertions;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Models.Timeline;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;
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
}

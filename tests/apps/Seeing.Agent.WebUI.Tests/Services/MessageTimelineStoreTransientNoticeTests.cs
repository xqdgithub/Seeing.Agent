using FluentAssertions;
using Seeing.Agent.WebUI.Models.Timeline;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class MessageTimelineStoreTransientNoticeTests
{
    [Fact]
    public void AppendTransientNotice_Should_Add_System_Item()
    {
        var store = new MessageTimelineStore();
        store.AppendTransientNotice("cmd:x", "**hello**");

        var item = store.Items.Should().ContainSingle(i => i.Key == "cmd:x").Subject;
        item.Kind.Should().Be(TimelineItemKind.System);
        item.Message!.Role.Should().Be("system");
        item.Message.Content.Should().Be("**hello**");
    }

    [Fact]
    public void AppendTransientNotice_Error_Should_Prefix_Marker()
    {
        var store = new MessageTimelineStore();
        store.AppendTransientNotice("cmd:err", "ACP 会话不可切换模型", isError: true);

        store.Items.Single(i => i.Key == "cmd:err").Message!.Content
            .Should().StartWith("❌");
    }

    [Fact]
    public void ResetFromSession_Should_Drop_Transient_Items()
    {
        var store = new MessageTimelineStore();
        store.AppendTransientNotice("cmd:x", "hello");

        store.ResetFromSession(Array.Empty<SessionMessage>(), "s1");

        store.Items.Should().NotContain(i => i.Key == "cmd:x");
    }
}

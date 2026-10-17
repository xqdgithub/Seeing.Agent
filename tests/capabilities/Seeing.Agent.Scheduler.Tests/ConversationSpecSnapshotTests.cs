using FluentAssertions;
using Seeing.Agent.Scheduler.Models;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

public class ConversationSpecSnapshotTests
{
    [Fact]
    public void TryCopyFromRoute_EmptyConversation_CopiesSessionRoute()
    {
        var target = new DispatchTarget { SessionId = "s-1" };

        var copied = ConversationSpec.TryCopyFromRoute(target, new SessionChannelRoute
        {
            ChannelId = "qq",
            Kind = "Group",
            ExternalId = "gr-1",
            Title = " 测试群 "
        });

        copied.Should().BeTrue();
        target.Conversation!.ChannelId.Should().Be("qq");
        target.Conversation.Kind.Should().Be("group");
        target.Conversation.ExternalId.Should().Be("gr-1");
        target.Conversation.Title.Should().Be("测试群");
    }

    [Fact]
    public void TryCopyFromRoute_ExistingConversation_DoesNotOverwrite()
    {
        var target = new DispatchTarget
        {
            SessionId = "s-1",
            Conversation = new ConversationSpec
            {
                ChannelId = "wecom",
                Kind = "direct",
                ExternalId = "u-1"
            }
        };

        var copied = ConversationSpec.TryCopyFromRoute(target, new SessionChannelRoute
        {
            ChannelId = "qq",
            Kind = "group",
            ExternalId = "gr-1"
        });

        copied.Should().BeFalse();
        target.Conversation!.ChannelId.Should().Be("wecom");
    }

    [Fact]
    public void TryCopyFromRoute_EmptyRoute_LeavesConversationUnset()
    {
        var target = new DispatchTarget { SessionId = "s-1" };

        ConversationSpec.TryCopyFromRoute(target, null).Should().BeFalse();
        ConversationSpec.TryCopyFromRoute(target, new SessionChannelRoute
        {
            ChannelId = "qq",
            Kind = "group",
            ExternalId = ""
        }).Should().BeFalse();
        target.Conversation.Should().BeNull();
    }
}

using FluentAssertions;
using Seeing.Agent.Abstractions.Channels;
using Xunit;

namespace Seeing.Agent.Abstractions.Tests.Channels;

public class ChannelCapabilitiesTests
{
    [Fact]
    public void ResolveAttachments_ShouldFallbackToDefault_WhenKindNotOverridden()
    {
        var caps = new ChannelCapabilities
        {
            SupportsReplyAnchor = true,
            Attachments = new() { Kinds = [OutboundAttachmentKind.File], MaxBytes = 10, MaxPerMessage = 2 }
        };

        caps.ResolveAttachments(ConversationKind.Group)!.Kinds.Should().Contain(OutboundAttachmentKind.File);
    }

    [Fact]
    public void ResolveAttachments_ShouldReturnNull_WhenChannelSupportsNoAttachments()
    {
        var caps = new ChannelCapabilities { SupportsReplyAnchor = false };
        caps.ResolveAttachments(ConversationKind.Direct).Should().BeNull();
    }

    [Fact]
    public void ResolveAttachments_ShouldUseFirstMatchingOverride()
    {
        var guild = new ChannelAttachmentCapabilities { Kinds = [OutboundAttachmentKind.Image], MaxBytes = 5, MaxPerMessage = 1 };
        var caps = new ChannelCapabilities
        {
            SupportsReplyAnchor = true,
            Attachments = new() { Kinds = [OutboundAttachmentKind.File], MaxBytes = 10, MaxPerMessage = 2 },
            AttachmentsByConversationKind = [new() { Kind = ConversationKind.Guild, Capabilities = guild }]
        };

        caps.ResolveAttachments(ConversationKind.Guild).Should().BeSameAs(guild);
    }
}

using FluentAssertions;
using Seeing.Agent.Abstractions.Channels;
using Xunit;

namespace Seeing.Agent.Abstractions.Tests.Channels;

public class ChannelCapabilitiesExtensionsTests
{
    [Fact]
    public void Supports_ShouldReturnTrue_ForDeclaredKind()
    {
        var caps = new ChannelAttachmentCapabilities
        {
            Kinds = [OutboundAttachmentKind.Image, OutboundAttachmentKind.File],
            MaxBytes = 10,
            MaxPerMessage = 2
        };

        caps.Supports(OutboundAttachmentKind.File).Should().BeTrue();
    }

    [Fact]
    public void Supports_ShouldReturnFalse_ForUndeclaredKind()
    {
        var caps = new ChannelAttachmentCapabilities
        {
            Kinds = [OutboundAttachmentKind.Image],
            MaxBytes = 10,
            MaxPerMessage = 2
        };

        caps.Supports(OutboundAttachmentKind.File).Should().BeFalse();
    }

    [Fact]
    public void Supports_ShouldReturnFalse_WhenKindsIsNull()
    {
        // required 只保证字段出现，不保证非 null（远程注册帧可显式传 null）
        var caps = new ChannelAttachmentCapabilities
        {
            Kinds = null!,
            MaxBytes = 10,
            MaxPerMessage = 2
        };

        caps.Supports(OutboundAttachmentKind.File).Should().BeFalse();
    }

    [Fact]
    public void ResolveAttachments_ShouldSkipNullOverride_AndFallback()
    {
        var fallback = new ChannelAttachmentCapabilities
        {
            Kinds = [OutboundAttachmentKind.File],
            MaxBytes = 10,
            MaxPerMessage = 2
        };
        var caps = new ChannelCapabilities
        {
            SupportsReplyAnchor = true,
            Attachments = fallback,
            AttachmentsByConversationKind = [new() { Kind = ConversationKind.Guild, Capabilities = null! }]
        };

        caps.ResolveAttachments(ConversationKind.Guild).Should().BeSameAs(fallback);
    }
}

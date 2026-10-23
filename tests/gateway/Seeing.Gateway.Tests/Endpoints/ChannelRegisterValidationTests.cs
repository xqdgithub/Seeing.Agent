using FluentAssertions;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Gateway.Endpoints;
using Xunit;

namespace Seeing.Gateway.Tests.Endpoints;

/// <summary>
/// 注册帧能力结构校验：边界只做 null 结构判定，避免残缺能力进入连接注册表。
/// handler 的「回 error + 不注册 + 不断连」端到端行为需要真实 WebSocket，留待 e2e。
/// </summary>
public class ChannelRegisterValidationTests
{
    [Fact]
    public void IsValid_CompleteCapabilities_ShouldBeTrue()
    {
        var capabilities = new ChannelCapabilities
        {
            SupportsReplyAnchor = true,
            Attachments = new()
            {
                Kinds = [OutboundAttachmentKind.Image, OutboundAttachmentKind.File],
                MaxBytes = 1024,
                MaxPerMessage = 2
            },
            AttachmentsByConversationKind =
            [
                new()
                {
                    Kind = ConversationKind.Guild,
                    Capabilities = new()
                    {
                        Kinds = [OutboundAttachmentKind.Image],
                        MaxBytes = 512,
                        MaxPerMessage = 1
                    }
                }
            ]
        };

        ChannelRegisterValidation.IsValid(capabilities).Should().BeTrue();
    }

    [Fact]
    public void IsValid_TextOnlyCapabilities_ShouldBeTrue()
    {
        var capabilities = new ChannelCapabilities
        {
            SupportsReplyAnchor = false,
            Attachments = null
        };

        ChannelRegisterValidation.IsValid(capabilities).Should().BeTrue();
    }

    [Fact]
    public void IsValid_NullCapabilities_ShouldBeFalse()
    {
        ChannelRegisterValidation.IsValid(null).Should().BeFalse();
    }

    [Fact]
    public void IsValid_NullAttachmentsKinds_ShouldBeFalse()
    {
        var capabilities = new ChannelCapabilities
        {
            SupportsReplyAnchor = true,
            Attachments = new()
            {
                Kinds = null!,
                MaxBytes = 1024,
                MaxPerMessage = 1
            }
        };

        ChannelRegisterValidation.IsValid(capabilities).Should().BeFalse();
    }

    [Fact]
    public void IsValid_OverrideWithNullCapabilities_ShouldBeFalse()
    {
        var capabilities = new ChannelCapabilities
        {
            SupportsReplyAnchor = true,
            AttachmentsByConversationKind =
            [
                new() { Kind = ConversationKind.Guild, Capabilities = null! }
            ]
        };

        ChannelRegisterValidation.IsValid(capabilities).Should().BeFalse();
    }

    [Fact]
    public void IsValid_OverrideWithNullKinds_ShouldBeFalse()
    {
        var capabilities = new ChannelCapabilities
        {
            SupportsReplyAnchor = true,
            AttachmentsByConversationKind =
            [
                new()
                {
                    Kind = ConversationKind.Guild,
                    Capabilities = new()
                    {
                        Kinds = null!,
                        MaxBytes = 512,
                        MaxPerMessage = 1
                    }
                }
            ]
        };

        ChannelRegisterValidation.IsValid(capabilities).Should().BeFalse();
    }
}

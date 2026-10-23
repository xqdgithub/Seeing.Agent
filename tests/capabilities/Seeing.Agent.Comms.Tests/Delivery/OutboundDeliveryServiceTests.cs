using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;
using Moq;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Comms.Delivery;
using Seeing.Agent.Comms.Directories;

namespace Seeing.Agent.Comms.Tests;

public class OutboundDeliveryServiceTests
{
    [Fact]
    public async Task DeliverAsync_UserNotInDirectory_ReturnsNotInDirectory()
    {
        var directory = new Mock<IChannelDirectory>();
        directory.Setup(d => d.FindUserAsync("qq", "u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ChannelUserEntry?)null);
        var svc = BuildDeliveryService(directory.Object, registry: new ChannelAdapterRegistry());

        var result = await svc.DeliverAsync(BuildPush("qq", "u1"));

        result.FailureKind.Should().Be(OutboundFailureKind.NotInDirectory);
    }

    [Fact]
    public async Task DeliverAsync_OptedOut_ReturnsOptedOut_AndInboundRegisterDoesNotClearIt()
    {
        var directory = new ChannelDirectoryService(
            new MemoryDirectoryStore(),
            Options.Create(new CommsOptions()));
        directory.RegisterInbound(Inbound("qq", "u1"));
        await directory.SetOptOutAsync("qq", "u1", true);
        directory.RegisterInbound(Inbound("qq", "u1"));

        var user = await directory.FindUserAsync("qq", "u1");
        user.Should().NotBeNull();
        user!.OptedOut.Should().BeTrue();

        var adapter = new Mock<IChannelAdapter>();
        adapter.Setup(a => a.ChannelId).Returns("qq");
        // Spec §11：所有适配器替身显式声明能力（required 成员，Moq 默认 null 会 NRE）
        adapter.SetupGet(a => a.Capabilities).Returns(new ChannelCapabilities { SupportsReplyAnchor = false });
        var registry = new ChannelAdapterRegistry();
        registry.Register(adapter.Object);
        var svc = BuildDeliveryService(directory, registry);

        var result = await svc.DeliverAsync(BuildPush("qq", "u1"));

        result.FailureKind.Should().Be(OutboundFailureKind.OptedOut);
        adapter.Verify(
            a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeliverAsync_RateLimited_AfterLimitReached()
    {
        var directory = new ChannelDirectoryService(
            new MemoryDirectoryStore(),
            Options.Create(new CommsOptions { RateLimitPerHour = 2 }));
        directory.RegisterInbound(Inbound("qq", "u1"));

        var adapter = new Mock<IChannelAdapter>();
        adapter.Setup(a => a.ChannelId).Returns("qq");
        // Spec §11：所有适配器替身显式声明能力（required 成员，Moq 默认 null 会 NRE）
        adapter.SetupGet(a => a.Capabilities).Returns(new ChannelCapabilities { SupportsReplyAnchor = false });
        adapter.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OutboundDeliveryResult.Ok());
        var registry = new ChannelAdapterRegistry();
        registry.Register(adapter.Object);
        var svc = BuildDeliveryService(directory, registry);

        (await svc.DeliverAsync(BuildPush("qq", "u1"))).Delivered.Should().BeTrue();
        (await svc.DeliverAsync(BuildPush("qq", "u1"))).Delivered.Should().BeTrue();
        var third = await svc.DeliverAsync(BuildPush("qq", "u1"));

        third.FailureKind.Should().Be(OutboundFailureKind.RateLimited);
        directory.IsRateLimited("qq", "u1").Should().BeTrue();
        adapter.Verify(
            a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task DeliverAsync_NoAdapterRegistered_ReturnsChannelUnavailable()
    {
        var directory = new Mock<IChannelDirectory>();
        directory.Setup(d => d.FindUserAsync("qq", "u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(User("qq", "u1"));
        directory.Setup(d => d.IsRateLimited("qq", "u1")).Returns(false);
        var svc = BuildDeliveryService(directory.Object, new ChannelAdapterRegistry());

        var result = await svc.DeliverAsync(BuildPush("qq", "u1"));

        result.FailureKind.Should().Be(OutboundFailureKind.ChannelUnavailable);
        directory.Verify(d => d.RecordDelivery(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeliverAsync_Success_CallsAdapter_AndNotifier_AndRecordsDelivery()
    {
        var directory = new Mock<IChannelDirectory>();
        directory.Setup(d => d.FindUserAsync("qq", "u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(User("qq", "u1"));
        directory.Setup(d => d.IsRateLimited("qq", "u1")).Returns(false);

        var message = BuildPush("qq", "u1") with
        {
            Source = "scheduler.cron",
            CorrelationSessionId = "s1",
            JobId = "j1",
            Target = new DeliveryTarget
            {
                Conversation = new ConversationInfo
                {
                    ChannelId = "qq",
                    Kind = ConversationKind.Direct,
                    ExternalId = "u1",
                    Title = "Ada",
                },
                ReplyToMsgId = "m1",
            },
        };

        OutboundMessage? seen = null;
        var adapter = new Mock<IChannelAdapter>();
        adapter.Setup(a => a.ChannelId).Returns("qq");
        // Spec §11：所有适配器替身显式声明能力（required 成员，Moq 默认 null 会 NRE）
        adapter.SetupGet(a => a.Capabilities).Returns(new ChannelCapabilities { SupportsReplyAnchor = false });
        adapter.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboundMessage, CancellationToken>((m, _) => seen = m)
            .ReturnsAsync(OutboundDeliveryResult.Ok());
        var registry = new ChannelAdapterRegistry();
        registry.Register(adapter.Object);

        var notifier = new Mock<IChannelDeliveryNotifier>();
        notifier.Setup(n => n.NotifyDeliveredAsync(
                It.IsAny<OutboundMessage>(),
                It.IsAny<OutboundDeliveryResult>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = BuildDeliveryService(directory.Object, registry, notifier.Object);
        var result = await svc.DeliverAsync(message);

        result.Delivered.Should().BeTrue();
        seen.Should().BeSameAs(message);
        notifier.Verify(n => n.NotifyDeliveredAsync(
            message,
            It.Is<OutboundDeliveryResult>(r => r.Delivered),
            It.IsAny<CancellationToken>()), Times.Once);
        directory.Verify(d => d.RecordDelivery("qq", "u1"), Times.Once);
    }

    [Fact]
    public async Task DeliverAsync_AdapterReturnsPlatformError_PassesThrough()
    {
        var directory = new Mock<IChannelDirectory>();
        directory.Setup(d => d.FindUserAsync("qq", "u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(User("qq", "u1"));
        var adapter = new Mock<IChannelAdapter>();
        adapter.Setup(a => a.ChannelId).Returns("qq");
        // Spec §11：所有适配器替身显式声明能力（required 成员，Moq 默认 null 会 NRE）
        adapter.SetupGet(a => a.Capabilities).Returns(new ChannelCapabilities { SupportsReplyAnchor = false });
        adapter.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OutboundDeliveryResult.Fail(OutboundFailureKind.PlatformError, "429"));
        var registry = new ChannelAdapterRegistry();
        registry.Register(adapter.Object);
        var notifier = new Mock<IChannelDeliveryNotifier>();
        var svc = BuildDeliveryService(directory.Object, registry, notifier.Object);

        var result = await svc.DeliverAsync(BuildPush("qq", "u1"));

        result.FailureKind.Should().Be(OutboundFailureKind.PlatformError);
        result.Detail.Should().Be("429");
        directory.Verify(d => d.RecordDelivery(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        notifier.Verify(n => n.NotifyDeliveredAsync(
            It.IsAny<OutboundMessage>(),
            It.IsAny<OutboundDeliveryResult>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeliverAsync_ShouldSucceed_ForTextOnly_WhenChannelSupportsNoAttachments()
    {
        var adapter = new Mock<IChannelAdapter>();
        adapter.SetupGet(a => a.ChannelId).Returns("wecom");
        adapter.SetupGet(a => a.Capabilities).Returns(new ChannelCapabilities { SupportsReplyAnchor = false });
        adapter.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OutboundDeliveryResult.Ok());
        var delivery = BuildDeliveryService(adapter.Object, DirectoryWith("wecom", "u1"));

        var result = await delivery.DeliverAsync(TextMessage("wecom", "u1", "hi"));

        result.Delivered.Should().BeTrue();
        adapter.Verify(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeliverAsync_ShouldRejectAttachment_WhenChannelSupportsNone()
    {
        // caps.Attachments == null；消息带 1 个附件
        var adapter = new Mock<IChannelAdapter>();
        adapter.SetupGet(a => a.ChannelId).Returns("wecom");
        adapter.SetupGet(a => a.Capabilities).Returns(new ChannelCapabilities { SupportsReplyAnchor = false });
        adapter.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OutboundDeliveryResult.Ok());
        var delivery = BuildDeliveryService(adapter.Object, DirectoryWith("wecom", "u1"));

        var result = await delivery.DeliverAsync(AttachmentMessage("wecom", "u1", FileAttachment(10)));

        result.FailureKind.Should().Be(OutboundFailureKind.AttachmentUnsupported);
        adapter.Verify(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeliverAsync_ShouldReject_WhenAttachmentCountExceedsCapability()
    {
        // caps: Kinds={File}, MaxBytes=1024, MaxPerMessage=1；消息带 2 个附件
        var adapter = new Mock<IChannelAdapter>();
        adapter.SetupGet(a => a.ChannelId).Returns("qq");
        adapter.SetupGet(a => a.Capabilities).Returns(new ChannelCapabilities
        {
            SupportsReplyAnchor = false,
            Attachments = new ChannelAttachmentCapabilities
            {
                Kinds = [OutboundAttachmentKind.File],
                MaxBytes = 1024,
                MaxPerMessage = 1,
            },
        });
        adapter.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OutboundDeliveryResult.Ok());
        var delivery = BuildDeliveryService(adapter.Object, DirectoryWith("qq", "u1"));

        var result = await delivery.DeliverAsync(
            AttachmentMessage("qq", "u1", FileAttachment(10), FileAttachment(20)));

        result.FailureKind.Should().Be(OutboundFailureKind.AttachmentUnsupported);
        result.Detail.Should().Contain("1");
        adapter.Verify(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeliverAsync_ShouldReject_WhenAttachmentSizeExceedsCapability()
    {
        // caps.MaxBytes = 1024；attachment.SizeBytes = 2048
        var adapter = new Mock<IChannelAdapter>();
        adapter.SetupGet(a => a.ChannelId).Returns("qq");
        adapter.SetupGet(a => a.Capabilities).Returns(new ChannelCapabilities
        {
            SupportsReplyAnchor = false,
            Attachments = new ChannelAttachmentCapabilities
            {
                Kinds = [OutboundAttachmentKind.File],
                MaxBytes = 1024,
                MaxPerMessage = 4,
            },
        });
        adapter.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OutboundDeliveryResult.Ok());
        var delivery = BuildDeliveryService(adapter.Object, DirectoryWith("qq", "u1"));

        var result = await delivery.DeliverAsync(AttachmentMessage("qq", "u1", FileAttachment(2048)));

        result.FailureKind.Should().Be(OutboundFailureKind.AttachmentTooLarge);
        adapter.Verify(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeliverAsync_ShouldReject_WhenSizeBytesIsNotPositive()
    {
        // attachment.SizeBytes = 0
        var adapter = new Mock<IChannelAdapter>();
        adapter.SetupGet(a => a.ChannelId).Returns("qq");
        adapter.SetupGet(a => a.Capabilities).Returns(new ChannelCapabilities
        {
            SupportsReplyAnchor = false,
            Attachments = new ChannelAttachmentCapabilities
            {
                Kinds = [OutboundAttachmentKind.File],
                MaxBytes = 1024,
                MaxPerMessage = 4,
            },
        });
        adapter.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OutboundDeliveryResult.Ok());
        var delivery = BuildDeliveryService(adapter.Object, DirectoryWith("qq", "u1"));

        var result = await delivery.DeliverAsync(AttachmentMessage("qq", "u1", FileAttachment(0)));

        result.FailureKind.Should().Be(OutboundFailureKind.AttachmentUnavailable);
        adapter.Verify(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeliverAsync_ShouldFillReplyAnchor_OnlyForReplyKind()
    {
        // 目录已登记 ReplyToMsgId = "m1"；caps.SupportsReplyAnchor = true
        var directory = new Mock<IChannelDirectory>();
        directory.Setup(d => d.FindUserAsync("qq", "u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(User("qq", "u1"));
        directory.Setup(d => d.IsRateLimited("qq", "u1")).Returns(false);
        directory.Setup(d => d.ResolveReplyAnchor(It.IsAny<ConversationInfo>())).Returns("m1");

        OutboundMessage? captured = null;
        var adapter = new Mock<IChannelAdapter>();
        adapter.SetupGet(a => a.ChannelId).Returns("qq");
        adapter.SetupGet(a => a.Capabilities).Returns(new ChannelCapabilities { SupportsReplyAnchor = true });
        adapter.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboundMessage, CancellationToken>((m, _) => captured = m)
            .ReturnsAsync(OutboundDeliveryResult.Ok());
        var delivery = BuildDeliveryService(adapter.Object, directory.Object);

        var reply = await delivery.DeliverAsync(
            TextMessage("qq", "u1", "hi", OutboundDeliveryKind.Reply));

        reply.Delivered.Should().BeTrue();
        captured!.Target.ReplyToMsgId.Should().Be("m1");

        captured = null;
        directory.Setup(d => d.ResolveReplyAnchor(It.IsAny<ConversationInfo>())).Returns("m2");
        var notice = await delivery.DeliverAsync(
            TextMessage("qq", "u1", "hi", OutboundDeliveryKind.SystemNotice));

        notice.Delivered.Should().BeTrue();
        captured!.Target.ReplyToMsgId.Should().BeNull();
    }

    [Fact]
    public async Task DeliverAsync_ShouldNotFillReplyAnchor_WhenChannelDoesNotSupportIt()
    {
        // 通道声明不支持被动回复：即使目录有锚点也不填，按主动消息发送
        var directory = new Mock<IChannelDirectory>();
        directory.Setup(d => d.FindUserAsync("wecom", "u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(User("wecom", "u1"));
        directory.Setup(d => d.IsRateLimited("wecom", "u1")).Returns(false);

        OutboundMessage? captured = null;
        var adapter = new Mock<IChannelAdapter>();
        adapter.SetupGet(a => a.ChannelId).Returns("wecom");
        adapter.SetupGet(a => a.Capabilities).Returns(new ChannelCapabilities { SupportsReplyAnchor = false });
        adapter.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboundMessage, CancellationToken>((m, _) => captured = m)
            .ReturnsAsync(OutboundDeliveryResult.Ok());
        var delivery = BuildDeliveryService(adapter.Object, directory.Object);

        var result = await delivery.DeliverAsync(
            TextMessage("wecom", "u1", "hi", OutboundDeliveryKind.Reply));

        result.Delivered.Should().BeTrue();
        captured!.Target.ReplyToMsgId.Should().BeNull();
        directory.Verify(d => d.ResolveReplyAnchor(It.IsAny<ConversationInfo>()), Times.Never);
    }

    [Fact]
    public async Task DeliverAsync_ShouldNotFillReplyAnchor_WhenExplicitAnchorPresent()
    {
        // 已显式携带 ReplyToMsgId 时不覆盖目录锚点
        var directory = new Mock<IChannelDirectory>();
        directory.Setup(d => d.FindUserAsync("qq", "u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(User("qq", "u1"));
        directory.Setup(d => d.IsRateLimited("qq", "u1")).Returns(false);
        directory.Setup(d => d.ResolveReplyAnchor(It.IsAny<ConversationInfo>())).Returns("m1");

        OutboundMessage? captured = null;
        var adapter = new Mock<IChannelAdapter>();
        adapter.SetupGet(a => a.ChannelId).Returns("qq");
        adapter.SetupGet(a => a.Capabilities).Returns(new ChannelCapabilities { SupportsReplyAnchor = true });
        adapter.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboundMessage, CancellationToken>((m, _) => captured = m)
            .ReturnsAsync(OutboundDeliveryResult.Ok());
        var delivery = BuildDeliveryService(adapter.Object, directory.Object);
        var message = TextMessage("qq", "u1", "hi", OutboundDeliveryKind.Reply) with
        {
            Target = new DeliveryTarget
            {
                Conversation = new ConversationInfo
                {
                    ChannelId = "qq",
                    Kind = ConversationKind.Direct,
                    ExternalId = "u1",
                },
                ReplyToMsgId = "explicit",
            },
        };

        var result = await delivery.DeliverAsync(message);

        result.Delivered.Should().BeTrue();
        captured.Should().BeSameAs(message);
        captured!.Target.ReplyToMsgId.Should().Be("explicit");
        directory.Verify(d => d.ResolveReplyAnchor(It.IsAny<ConversationInfo>()), Times.Never);
    }

    [Fact]
    public async Task DeliverAsync_ShouldThrow_WhenContentHasNeitherTextNorAttachments()
    {
        // Spec 5.5 不变式：文本与附件至少一个非空，否则属于生产端缺陷（fail fast）
        var svc = BuildDeliveryService(new Mock<IChannelDirectory>().Object, new ChannelAdapterRegistry());
        var message = BuildPush("qq", "u1") with { Content = new OutboundContent() };

        var act = async () => await svc.DeliverAsync(message);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private static OutboundDeliveryService BuildDeliveryService(
        IChannelDirectory directory,
        IChannelAdapterRegistry registry,
        IChannelDeliveryNotifier? notifier = null)
        => new(directory, registry, notifier ?? new NoopDeliveryNotifier());

    private static OutboundDeliveryService BuildDeliveryService(
        IChannelAdapter adapter,
        IChannelDirectory directory)
    {
        var registry = new ChannelAdapterRegistry();
        registry.Register(adapter);
        return BuildDeliveryService(directory, registry);
    }

    /// <summary>仅登记单个直聊用户的目录；其余目录查询保持 Moq 默认值。</summary>
    private static IChannelDirectory DirectoryWith(string channelId, string userId)
    {
        var directory = new Mock<IChannelDirectory>();
        directory.Setup(d => d.FindUserAsync(channelId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(User(channelId, userId));
        directory.Setup(d => d.IsRateLimited(channelId, userId)).Returns(false);
        return directory.Object;
    }

    private static OutboundMessage TextMessage(
        string channelId,
        string userId,
        string text,
        OutboundDeliveryKind kind = OutboundDeliveryKind.Push)
        => new()
        {
            Target = new DeliveryTarget
            {
                Conversation = new ConversationInfo
                {
                    ChannelId = channelId,
                    Kind = ConversationKind.Direct,
                    ExternalId = userId,
                },
            },
            DeliveryKind = kind,
            Content = new OutboundContent { Text = text },
        };

    private static OutboundMessage AttachmentMessage(
        string channelId,
        string userId,
        params OutboundAttachment[] attachments)
        => new()
        {
            Target = new DeliveryTarget
            {
                Conversation = new ConversationInfo
                {
                    ChannelId = channelId,
                    Kind = ConversationKind.Direct,
                    ExternalId = userId,
                },
            },
            DeliveryKind = OutboundDeliveryKind.Push,
            Content = new OutboundContent { Attachments = attachments },
        };

    private static OutboundAttachment FileAttachment(long sizeBytes)
        => new()
        {
            Kind = OutboundAttachmentKind.File,
            LocalPath = "/workspace/report.pdf",
            SizeBytes = sizeBytes,
        };

    private static OutboundMessage BuildPush(string channelId, string userId)
        => new()
        {
            Target = new DeliveryTarget
            {
                Conversation = new ConversationInfo
                {
                    ChannelId = channelId,
                    Kind = ConversationKind.Direct,
                    ExternalId = userId,
                },
            },
            DeliveryKind = OutboundDeliveryKind.Push,
            Content = new OutboundContent { Text = "hello" },
        };

    private static ChannelInboundMessage Inbound(string channelId, string userId)
        => new()
        {
            Sender = new ChannelUserIdentity
            {
                ChannelId = channelId,
                ExternalId = userId,
                DisplayName = "Ada",
            },
            Conversation = new ConversationInfo
            {
                ChannelId = channelId,
                Kind = ConversationKind.Direct,
                ExternalId = userId,
            },
            SessionId = "s",
            Text = "hi",
        };

    private static ChannelUserEntry User(string channelId, string externalId)
        => new()
        {
            ChannelId = channelId,
            ExternalId = externalId,
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            OptedOut = false,
        };
}

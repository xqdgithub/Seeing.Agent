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

    private static OutboundDeliveryService BuildDeliveryService(
        IChannelDirectory directory,
        IChannelAdapterRegistry registry,
        IChannelDeliveryNotifier? notifier = null)
        => new(directory, registry, notifier ?? new NoopDeliveryNotifier());

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

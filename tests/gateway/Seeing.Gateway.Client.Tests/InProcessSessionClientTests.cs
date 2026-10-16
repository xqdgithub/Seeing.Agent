using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Gateway.Models;
using System.Runtime.CompilerServices;
using Xunit;

namespace Seeing.Gateway.Client.Tests;

public sealed class InProcessSessionClientTests
{
    [Fact]
    public async Task SubmitAsync_ShouldDeliverHumanInboxWithConversation()
    {
        InboxMessage? captured = null;
        var runtime = new Mock<ISessionRuntime>();
        runtime
            .Setup(r => r.DeliverAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, CancellationToken>((message, _) => captured = message)
            .ReturnsAsync(InboxResult.Ok(pendingInputCount: 2));

        var conversation = new ConversationInfo
        {
            ChannelId = "qq",
            Kind = ConversationKind.Group,
            ExternalId = "g1"
        };
        var client = CreateClient(runtime.Object);

        var ack = await client.SubmitAsync(new GatewayRequest
        {
            SessionId = "ses",
            ChannelId = "qq",
            Sender = new ChannelUserIdentity { ChannelId = "qq", ExternalId = "u1" },
            Conversation = conversation,
            ReplyToMsgId = "m1",
            Input = [new GatewayTextContentPart("hello")]
        }, TestContext.Current.CancellationToken);

        ack.Success.Should().BeTrue();
        ack.PendingInputCount.Should().Be(2);
        captured.Should().NotBeNull();
        captured!.Sender.Kind.Should().Be(InboxSenderKind.Human);
        captured.Sender.Id.Should().Be("u1");
        captured.Conversation.Should().BeSameAs(conversation);
        captured.ReplyToMsgId.Should().Be("m1");
        captured.Body.Should().Be("hello");
        captured.Delivery.Should().Be(InboxDelivery.Wake);
    }

    [Fact]
    public async Task SubmitAsync_WhenSenderMissing_ShouldUseGatewayId()
    {
        InboxMessage? captured = null;
        var runtime = new Mock<ISessionRuntime>();
        runtime
            .Setup(r => r.DeliverAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboxMessage, CancellationToken>((message, _) => captured = message)
            .ReturnsAsync(InboxResult.Ok());

        var client = CreateClient(runtime.Object);
        await client.SubmitAsync(new GatewayRequest { SessionId = "ses" }, TestContext.Current.CancellationToken);

        captured!.Sender.Id.Should().Be("gateway");
    }

    [Fact]
    public async Task SubscribeAsync_ShouldMapRuntimeEventsViaTryMap()
    {
        var runtime = new Mock<ISessionRuntime>();
        runtime
            .Setup(r => r.SubscribeEvents(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => OneEvent(new LoopStartEvent
            {
                SessionId = "ses",
                LoopId = "loop-1",
                UserInput = "hi"
            }));
        runtime
            .Setup(r => r.WaitForIdleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var client = CreateClient(runtime.Object);
        var events = new List<GatewayEvent>();
        await foreach (var gatewayEvent in client.SubscribeAsync("ses", TestContext.Current.CancellationToken))
            events.Add(gatewayEvent);

        events.Should().ContainSingle();
        events[0].Object.Should().Be(GatewayEventObject.Response);
        events[0].Status.Should().Be(GatewayEventStatus.InProgress);
        events[0].SessionId.Should().Be("ses");
        events[0].LoopId.Should().Be("loop-1");
        events[0].SourceType.Should().Be(MessageEventType.LoopStart);
        events[0].Data!.UserInput.Should().Be("hi");
    }

    [Fact]
    public async Task CancelAsync_ShouldInterruptRuntime()
    {
        var runtime = new Mock<ISessionRuntime>();
        runtime
            .Setup(r => r.InterruptAsync("ses", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var client = CreateClient(runtime.Object);
        var cancelled = await client.CancelAsync("ses", TestContext.Current.CancellationToken);

        cancelled.Should().BeTrue();
        runtime.Verify(r => r.InterruptAsync("ses", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RespondPermissionAsync_ShouldTryResolveInScope()
    {
        var manager = new Mock<IPermissionRequestManager>();
        manager
            .Setup(m => m.TryResolve(
                "perm",
                PermissionEffect.Allow,
                PermissionGrantScope.Once,
                PermissionResolvedBy.User,
                "ok",
                "ses"))
            .Returns(true);

        var services = new ServiceCollection();
        services.AddSingleton(manager.Object);
        var client = CreateClient(new Mock<ISessionRuntime>().Object, services.BuildServiceProvider());

        var result = await client.RespondPermissionAsync(
            "ses", "perm", allow: true, reason: "ok", TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
    }

    private static InProcessSessionClient CreateClient(ISessionRuntime runtime, IServiceProvider? services = null)
    {
        services ??= new ServiceCollection().BuildServiceProvider();
        return new InProcessSessionClient(
            runtime,
            services,
            new GatewayRunTracker(),
            new Mock<IPermissionSurfaceRegistry>().Object,
            NullLogger<InProcessSessionClient>.Instance);
    }

    private static async IAsyncEnumerable<IMessageEvent> OneEvent(
        IMessageEvent messageEvent,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return messageEvent;
        await Task.CompletedTask;
    }
}

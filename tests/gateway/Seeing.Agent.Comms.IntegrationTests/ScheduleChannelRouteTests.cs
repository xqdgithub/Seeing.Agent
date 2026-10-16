using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Abstractions.Reminders;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Agent.Comms.IntegrationTests.Support;
using Seeing.Agent.Comms.Scheduling;
using Seeing.Agent.Scheduler.Execution;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Comms.IntegrationTests;

public class ScheduleChannelRouteTests
{
    [Fact]
    public async Task GroupRoute_OutboundOnly_DoesNotInject()
    {
        var sent = await Dispatch("sess-group", "group", "g1");

        sent.Runtime.Messages.Should().BeEmpty();
        sent.Adapter.Sent.Should().ContainSingle();
        var message = sent.Adapter.Sent[0];
        message.DeliveryKind.Should().Be(OutboundDeliveryKind.SystemNotice);
        message.CorrelationSessionId.Should().Be("sess-group");
        message.Target.Conversation.Kind.Should().Be(ConversationKind.Group);
        message.Target.Conversation.ExternalId.Should().Be("g1");
        message.Target.RecipientUserId.Should().BeNull();
    }

    [Fact]
    public async Task DirectRoute_OutboundOnly_TargetsDirectConversation()
    {
        var sent = await Dispatch("sess-dm", "direct", "u1");

        sent.Runtime.Messages.Should().BeEmpty();
        sent.Adapter.Sent.Should().ContainSingle();
        var message = sent.Adapter.Sent[0];
        message.Target.Conversation.Kind.Should().Be(ConversationKind.Direct);
        message.Target.Conversation.ExternalId.Should().Be("u1");
        message.Target.RecipientUserId.Should().Be("u1");
    }

    [Fact]
    public async Task RotatedSessionId_StillHitsChannelRoute()
    {
        var sent = await Dispatch("qq_group_g1_20261016214400", "group", "g1");

        sent.Runtime.Messages.Should().BeEmpty();
        var conversation = sent.Adapter.Sent.Should().ContainSingle().Subject.Target.Conversation;
        conversation.ExternalId.Should().Be("g1");
        conversation.ExternalId.Should().NotBe("qq_group_g1_20261016214400");
    }

    [Fact]
    public async Task SessionScheduleDispatcher_Injects_ChannelDispatcherDoesNot()
    {
        var runtime = new RecordingRuntime();
        var session = SessionData.Create();
        session.Id = "sess-inject";
        var sessionDispatcher = new SessionScheduleDispatcher(
            runtime,
            CommsIntegrationFixture.SessionManager(session),
            Mock.Of<ISystemReminderRenderer>(),
            NullLogger<SessionScheduleDispatcher>.Instance);

        var result = await sessionDispatcher.DispatchAsync(new DispatchRequest
        {
            Source = "cron",
            TaskType = "agent",
            Content = "written",
            SessionId = "sess-inject"
        }, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        runtime.Messages.Should().ContainSingle();
        runtime.Messages[0].Delivery.Should().Be(InboxDelivery.Inject);
        runtime.Messages[0].TargetSessionId.Should().Be("sess-inject");
    }

    private static async Task<Sent> Dispatch(string sessionId, string kind, string externalId)
    {
        var directory = CommsIntegrationFixture.Directory();
        var conversationKind = kind == "group" ? ConversationKind.Group : ConversationKind.Direct;
        directory.RegisterInbound(CommsIntegrationFixture.Inbound(conversationKind, externalId, sessionId));
        var (delivery, adapter) = CommsIntegrationFixture.Delivery(directory);

        var session = SessionData.Create();
        session.Id = sessionId;
        session.ChannelRoute = new SessionChannelRoute
        {
            ChannelId = "qq",
            Kind = kind,
            ExternalId = externalId,
            Title = kind == "group" ? "room" : null
        };

        var runtime = new RecordingRuntime();
        var dispatcher = new ChannelDeliveryScheduleDispatcher(
            runtime,
            CommsIntegrationFixture.SessionManager(session),
            Mock.Of<ISystemReminderRenderer>(),
            delivery,
            NullLogger<ChannelDeliveryScheduleDispatcher>.Instance);

        var result = await dispatcher.DispatchAsync(new DispatchRequest
        {
            Source = "cron",
            TaskType = "agent",
            Content = "push",
            SessionId = sessionId,
            Channel = "ignored",
            UserId = "ignored-user"
        }, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        return new Sent(runtime, adapter);
    }

    private sealed record Sent(RecordingRuntime Runtime, MemoryChannelAdapter Adapter);
}

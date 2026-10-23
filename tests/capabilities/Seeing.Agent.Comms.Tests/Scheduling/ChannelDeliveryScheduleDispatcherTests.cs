using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Reminders;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Agent.Comms.Delivery;
using Seeing.Agent.Comms.Extensions;
using Seeing.Agent.Comms.Scheduling;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Comms.Tests.Scheduling;

public class ChannelDeliveryScheduleDispatcherTests
{
    [Fact]
    public async Task Dispatch_WithChannelRoute_DeliversSystemNoticeMirroringRoute()
    {
        var session = SessionData.Create();
        session.Id = "sess-1";
        session.ChannelRoute = new SessionChannelRoute
        {
            ChannelId = "qq",
            Kind = "group",
            ExternalId = "g1",
            Title = "group-title"
        };

        OutboundMessage? captured = null;
        var delivery = new Mock<OutboundDeliveryService>(
            Mock.Of<IChannelDirectory>(),
            Mock.Of<IChannelAdapterRegistry>(),
            Mock.Of<IChannelDeliveryNotifier>(),
            NullLogger<OutboundDeliveryService>.Instance);
        delivery
            .Setup(d => d.DeliverAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboundMessage, CancellationToken>((message, _) => captured = message)
            .ReturnsAsync(OutboundDeliveryResult.Ok());

        var runtime = new RecordingRuntime();
        var dispatcher = new ChannelDeliveryScheduleDispatcher(
            runtime,
            SessionManager(session),
            Mock.Of<ISystemReminderRenderer>(),
            delivery.Object,
            NullLogger<ChannelDeliveryScheduleDispatcher>.Instance);

        var result = await dispatcher.DispatchAsync(new DispatchRequest
        {
            Source = "cron",
            TaskType = "agent",
            Content = "hello",
            SessionId = "sess-1",
            Channel = "ignored",
            UserId = "ignored-user",
            Metadata = new Dictionary<string, object> { ["jobId"] = "job-9" }
        });

        result.Success.Should().BeTrue();
        runtime.Messages.Should().BeEmpty();
        captured.Should().NotBeNull();
        captured!.DeliveryKind.Should().Be(OutboundDeliveryKind.SystemNotice);
        captured.CorrelationSessionId.Should().Be("sess-1");
        captured.Content.Text.Should().Be("hello");
        captured.JobId.Should().Be("job-9");
        captured.Target.Conversation.ChannelId.Should().Be("qq");
        captured.Target.Conversation.Kind.Should().Be(ConversationKind.Group);
        captured.Target.Conversation.ExternalId.Should().Be("g1");
        captured.Target.Conversation.Title.Should().Be("group-title");
    }

    [Fact]
    public async Task Dispatch_NoChannelRoute_SkipsOutbound_StillInjects()
    {
        var session = SessionData.Create();
        session.Id = "sess-2";
        session.ChannelRoute = null;

        var delivery = new Mock<OutboundDeliveryService>(
            Mock.Of<IChannelDirectory>(),
            Mock.Of<IChannelAdapterRegistry>(),
            Mock.Of<IChannelDeliveryNotifier>(),
            NullLogger<OutboundDeliveryService>.Instance);
        var runtime = new RecordingRuntime();
        var dispatcher = new ChannelDeliveryScheduleDispatcher(
            runtime,
            SessionManager(session),
            Mock.Of<ISystemReminderRenderer>(),
            delivery.Object);

        var result = await dispatcher.DispatchAsync(new DispatchRequest
        {
            Source = "cron",
            TaskType = "agent",
            Content = "hello",
            SessionId = "sess-2",
            Channel = "qq",
            UserId = "u1"
        });

        result.Success.Should().BeTrue();
        runtime.Messages.Should().BeEmpty();
        delivery.Verify(
            d => d.DeliverAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Dispatch_SuppressConsolePush_SkipsOutbound_StillInjectsReminder()
    {
        var session = SessionData.Create();
        session.Id = "sess-3";
        session.ChannelRoute = new SessionChannelRoute
        {
            ChannelId = "qq",
            Kind = "direct",
            ExternalId = "u1"
        };

        var reminders = new Mock<ISystemReminderRenderer>();
        reminders
            .Setup(r => r.TryParse("wrapped", out It.Ref<SystemReminderParts>.IsAny))
            .Returns((string _, out SystemReminderParts parts) =>
            {
                parts = new SystemReminderParts("job", "heartbeat", "notice", "plain-task");
                return true;
            });

        var delivery = new Mock<OutboundDeliveryService>(
            Mock.Of<IChannelDirectory>(),
            Mock.Of<IChannelAdapterRegistry>(),
            Mock.Of<IChannelDeliveryNotifier>(),
            NullLogger<OutboundDeliveryService>.Instance);
        var runtime = new RecordingRuntime();
        var dispatcher = new ChannelDeliveryScheduleDispatcher(
            runtime,
            SessionManager(session),
            reminders.Object,
            delivery.Object);

        var result = await dispatcher.DispatchAsync(new DispatchRequest
        {
            Source = "cron",
            TaskType = "agent",
            Content = "output",
            UserInput = "wrapped",
            SessionId = "sess-3",
            Metadata = new Dictionary<string, object> { ["suppress_console_push"] = true }
        });

        result.Success.Should().BeTrue();
        runtime.Messages.Should().BeEmpty();
        delivery.Verify(
            d => d.DeliverAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Dispatch_JobConversation_WithoutSessionRoute_DeliversToJobConversation()
    {
        // 目标矩阵（2026-10-17 设计）：任务级 conversation 齐备即出站，会话无 ChannelRoute 不再跳过
        var session = SessionData.Create();
        session.Id = "sess-4";
        session.ChannelRoute = null;

        OutboundMessage? captured = null;
        var delivery = new Mock<OutboundDeliveryService>(
            Mock.Of<IChannelDirectory>(),
            Mock.Of<IChannelAdapterRegistry>(),
            Mock.Of<IChannelDeliveryNotifier>(),
            NullLogger<OutboundDeliveryService>.Instance);
        delivery
            .Setup(d => d.DeliverAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboundMessage, CancellationToken>((message, _) => captured = message)
            .ReturnsAsync(OutboundDeliveryResult.Ok());

        var dispatcher = new ChannelDeliveryScheduleDispatcher(
            new RecordingRuntime(),
            SessionManager(session),
            Mock.Of<ISystemReminderRenderer>(),
            delivery.Object,
            NullLogger<ChannelDeliveryScheduleDispatcher>.Instance);

        var result = await dispatcher.DispatchAsync(new DispatchRequest
        {
            Source = "cron",
            TaskType = "agent",
            Content = "播报",
            SessionId = "sess-4",
            Conversation = new DispatchConversation
            {
                ChannelId = "qq",
                Kind = "group",
                ExternalId = "gr-100",
                Title = "测试群"
            }
        });

        result.Success.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.Target.Conversation.ChannelId.Should().Be("qq");
        captured.Target.Conversation.Kind.Should().Be(ConversationKind.Group);
        captured.Target.Conversation.ExternalId.Should().Be("gr-100");
        captured.Target.Conversation.Title.Should().Be("测试群");
    }

    [Fact]
    public async Task Dispatch_JobConversation_OverSessionRoute_PrefersJobConversation()
    {
        // 出站优先级：任务级 conversation > 会话 ChannelRoute
        var session = SessionData.Create();
        session.Id = "sess-5";
        session.ChannelRoute = new SessionChannelRoute
        {
            ChannelId = "wecom",
            Kind = "direct",
            ExternalId = "u-legacy"
        };

        OutboundMessage? captured = null;
        var delivery = new Mock<OutboundDeliveryService>(
            Mock.Of<IChannelDirectory>(),
            Mock.Of<IChannelAdapterRegistry>(),
            Mock.Of<IChannelDeliveryNotifier>(),
            NullLogger<OutboundDeliveryService>.Instance);
        delivery
            .Setup(d => d.DeliverAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboundMessage, CancellationToken>((message, _) => captured = message)
            .ReturnsAsync(OutboundDeliveryResult.Ok());

        var dispatcher = new ChannelDeliveryScheduleDispatcher(
            new RecordingRuntime(),
            SessionManager(session),
            Mock.Of<ISystemReminderRenderer>(),
            delivery.Object,
            NullLogger<ChannelDeliveryScheduleDispatcher>.Instance);

        var result = await dispatcher.DispatchAsync(new DispatchRequest
        {
            Source = "cron",
            TaskType = "agent",
            Content = "播报",
            SessionId = "sess-5",
            Conversation = new DispatchConversation
            {
                ChannelId = "qq",
                Kind = "group",
                ExternalId = "gr-new"
            }
        });

        result.Success.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.Target.Conversation.ChannelId.Should().Be("qq");
        captured.Target.Conversation.ExternalId.Should().Be("gr-new");
        captured.Target.RecipientUserId.Should().BeNull(because: "group 目标无 RecipientUserId");
    }

    [Fact]
    public void MoveCompositeScheduleDispatcherLast_KeepsCompositeAsResolvedDispatcher()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IScheduledJobDispatcher>(new NamedDispatcher("a"));
        services.AddSingleton<IScheduledJobDispatcher, CompositeScheduleDispatcher>();
        services.AddSingleton<IScheduledJobDispatcher>(new NamedDispatcher("b"));

        CommsServiceCollectionExtensions.MoveCompositeScheduleDispatcherLast(services);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IScheduledJobDispatcher>()
            .Should().BeOfType<CompositeScheduleDispatcher>();
    }

    private static ISessionManager SessionManager(SessionData session)
    {
        var mock = new Mock<ISessionManager>();
        mock.Setup(m => m.EnsureSessionAsync(
                session.Id,
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()))
            .ReturnsAsync(session);
        return mock.Object;
    }

    private sealed class RecordingRuntime : ISessionRuntime
    {
        public List<InboxMessage> Messages { get; } = [];

        public Task<InboxResult> DeliverAsync(InboxMessage message, CancellationToken ct = default)
        {
            Messages.Add(message);
            return Task.FromResult(InboxResult.Ok());
        }

        public Task<bool> InterruptAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<int> StopAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> CancelAllInFlightAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task WaitForIdleAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
        public SessionExecutionOverview GetOverview(string sessionId) => new();
        public bool HasAnyActiveExecution() => false;
        public IReadOnlyList<Seeing.Agent.Abstractions.Events.IMessageEvent> GetBufferedEvents(string sessionId) => [];

        public async IAsyncEnumerable<Seeing.Agent.Abstractions.Events.IMessageEvent> SubscribeEvents(
            string sessionId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<int> ReconcileIncompleteTasksAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class NamedDispatcher(string name) : IScheduledJobDispatcher
    {
        public string Name { get; } = name;

        public Task<DispatchResult> DispatchAsync(DispatchRequest request, CancellationToken ct = default) =>
            Task.FromResult(DispatchResult.Ok());
    }

    private sealed class CompositeScheduleDispatcher : IScheduledJobDispatcher
    {
        public Task<DispatchResult> DispatchAsync(DispatchRequest request, CancellationToken ct = default) =>
            Task.FromResult(DispatchResult.Ok());
    }
}

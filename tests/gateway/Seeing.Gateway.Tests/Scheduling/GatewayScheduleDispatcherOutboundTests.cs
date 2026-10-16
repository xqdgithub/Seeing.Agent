using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Reminders;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Agent.Gateway.Scheduling;
using Seeing.Gateway.Protocol;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Gateway.Tests.Scheduling;

public class GatewayScheduleDispatcherOutboundTests
{
    [Fact]
    public async Task Dispatch_UsesSessionChannelId_NotRequestChannel()
    {
        var session = SessionData.Create();
        session.Id = "sess-1";
        session.ChannelId = "qq";
        session.UserId = "u1";

        var sessionManager = CreateSessionManager(session);
        GatewayChannelOutboundPayload? captured = null;

        var dispatcher = new GatewayScheduleDispatcher(
            new StubSessionRuntime(),
            sessionManager.Object,
            Mock.Of<ISystemReminderRenderer>(),
            NullLogger<GatewayScheduleDispatcher>.Instance,
            connectionManager: null,
            pushChannelOutbound: payload =>
            {
                captured = payload;
                return true;
            });

        var result = await dispatcher.DispatchAsync(new DispatchRequest
        {
            Source = "cron",
            TaskType = "agent",
            Content = "hello",
            SessionId = "sess-1",
            Channel = "ignored",
            UserId = "ignored-user"
        }, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.Channel.Should().Be("qq");
        captured.UserId.Should().Be("u1");
        captured.SessionId.Should().Be("sess-1");
        captured.Text.Should().Be("hello");
    }

    [Fact]
    public async Task Dispatch_NoSessionChannel_SkipsOutbound_StillOk()
    {
        var session = SessionData.Create();
        session.Id = "sess-2";
        session.ChannelId = null;
        session.UserId = null;

        var sessionManager = CreateSessionManager(session);
        var outboundCalls = 0;

        var dispatcher = new GatewayScheduleDispatcher(
            new StubSessionRuntime(),
            sessionManager.Object,
            Mock.Of<ISystemReminderRenderer>(),
            NullLogger<GatewayScheduleDispatcher>.Instance,
            connectionManager: null,
            pushChannelOutbound: _ =>
            {
                outboundCalls++;
                return true;
            });

        var result = await dispatcher.DispatchAsync(new DispatchRequest
        {
            Source = "cron",
            TaskType = "agent",
            Content = "hello",
            SessionId = "sess-2",
            Channel = "qq",
            UserId = "u1"
        }, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        outboundCalls.Should().Be(0);
    }

    private static Mock<ISessionManager> CreateSessionManager(SessionData session)
    {
        var mock = new Mock<ISessionManager>();
        mock.Setup(m => m.EnsureSessionAsync(session.Id, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(session);
        mock.Setup(m => m.Get(session.Id)).Returns(session);
        mock.Setup(m => m.AddMessageAsync(session.Id, It.IsAny<SessionMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    private sealed class StubSessionRuntime : ISessionRuntime
    {
        public Task<InboxResult> DeliverAsync(InboxMessage message, CancellationToken ct = default) =>
            Task.FromResult(InboxResult.Ok());

        public Task<bool> InterruptAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(false);

        public Task<int> StopAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(0);

        public Task<int> CancelAllInFlightAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task WaitForIdleAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;

        public SessionExecutionOverview GetOverview(string sessionId) => new();

        public bool HasAnyActiveExecution() => false;

        public IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId) => Array.Empty<IMessageEvent>();

        public async IAsyncEnumerable<IMessageEvent> SubscribeEvents(
            string sessionId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<int> ReconcileIncompleteTasksAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(0);
    }
}

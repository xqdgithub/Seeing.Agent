using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Chat;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Gateway.Configuration;
using Seeing.Agent.Gateway.Core;
using Seeing.Gateway.Models;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Gateway.Tests.Core;

/// <summary>
/// G3：外部客户端订阅执行流时 Attach/Detach Presence（含异常路径）。
/// </summary>
public class GatewayOrchestratorPresenceTests
{
    [Fact]
    public async Task SubscribeExecutionEvents_ShouldAttachAndDetachPresence()
    {
        var presence = new Mock<IPermissionPresenceStore>();
        var orchestrator = CreateOrchestrator(BuildProvider(presence, EmptyEventsAsync(TestContext.Current.CancellationToken)));

        await foreach (var _ in orchestrator.SubscribeExecutionEventsAsync("ses_1", "exec_1", TestContext.Current.CancellationToken))
        {
        }

        presence.Verify(p => p.Attach("ses_1"), Times.Once);
        presence.Verify(p => p.Detach("ses_1"), Times.Once);
    }

    [Fact]
    public async Task SubscribeExecutionEvents_WhenStreamFaults_ShouldStillDetachPresence()
    {
        var presence = new Mock<IPermissionPresenceStore>();
        var orchestrator = CreateOrchestrator(BuildProvider(presence, ThrowingEventsAsync(TestContext.Current.CancellationToken)));

        var events = new List<GatewayEvent>();
        await foreach (var e in orchestrator.SubscribeExecutionEventsAsync("ses_1", "exec_1", TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        events.Should().Contain(e => e.Object == GatewayEventObject.Error);
        presence.Verify(p => p.Attach("ses_1"), Times.Once);
        presence.Verify(p => p.Detach("ses_1"), Times.Once);
    }

    private static ServiceProvider BuildProvider(
        Mock<IPermissionPresenceStore> presence,
        IAsyncEnumerable<IMessageEvent> events)
    {
        var chatOrchestrator = new Mock<IChatOrchestrator>();
        chatOrchestrator
            .Setup(c => c.SubscribeEvents(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(events);
        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(s => s.SaveAsync(It.IsAny<string>())).Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton(presence.Object);
        services.AddSingleton(chatOrchestrator.Object);
        services.AddSingleton(sessionManager.Object);
        return services.BuildServiceProvider();
    }

    private static GatewayOrchestratorV2 CreateOrchestrator(IServiceProvider provider) =>
        new(
            provider,
            new GatewayOptions(),
            new GatewayRunTracker(),
            new SessionExecutionQueue(),
            NullLogger<GatewayOrchestratorV2>.Instance);

    private static async IAsyncEnumerable<IMessageEvent> EmptyEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<IMessageEvent> ThrowingEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        throw new InvalidOperationException("stream boom");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}

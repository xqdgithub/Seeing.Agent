using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Gateway.Core;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Agent.Tests.Gateway;

public class GatewaySessionServiceTests
{
    [Fact]
    public async Task ResetAsync_ShouldClearMessagesAndResetSelectedAgent()
    {
        var registryMock = new Mock<IAgentRegistry>();
        registryMock
            .Setup(r => r.GetDefaultAgentNameAsync())
            .ReturnsAsync("build");

        var manager = CreateSessionManager();
        var session = await manager.EnsureSessionAsync("test-session", selectedAgent: "acp-opencode");
        session.AddMessage(SessionMessage.UserMessage("hello"));
        session.AddMessage(SessionMessage.AssistantMessage("hi"));
        await manager.SaveAsync(session.Id);

        var service = new GatewaySessionService(manager, registryMock.Object);
        var result = await service.ResetAsync(session.Id);

        result.Should().NotBeNull();
        result!.SessionId.Should().Be("test-session");
        result.Cleared.Should().BeTrue();
        result.MessageCount.Should().Be(0);

        var loaded = manager.Get(session.Id);
        loaded.Should().NotBeNull();
        loaded!.Messages.Should().BeEmpty();
        loaded.SelectedAgent.Should().Be("build");
    }

    [Fact]
    public async Task ResetAsync_ShouldReturnNullWhenSessionMissing()
    {
        var registryMock = new Mock<IAgentRegistry>();
        var manager = CreateSessionManager();
        var service = new GatewaySessionService(manager, registryMock.Object);

        var result = await service.ResetAsync("missing-session");

        result.Should().BeNull();
    }

    private static SessionManager CreateSessionManager()
    {
        var store = new InMemorySessionStore();
        return new SessionManager(
            store: store,
            logger: NullLogger<SessionManager>.Instance);
    }
}

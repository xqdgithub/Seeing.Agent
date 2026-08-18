using Seeing.Agent.Abstractions.Agents;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Gateway.Core;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Agent.Tests.Gateway;

public class GatewaySessionServiceTests
{
    [Fact]
    public async Task ResetAsync_ShouldClearMessagesAndResetSelectedAgentAndDefaultModel()
    {
        var registryMock = new Mock<IAgentRegistry>();
        var runtimeMock = new Mock<IAgentRuntimeManager>();
        runtimeMock
            .Setup(r => r.GetDefaultAgentNameAsync())
            .ReturnsAsync("build");
        var buildAgent = new AgentDefinition { Name = "build", Runtime = AgentRuntime.Native };
        registryMock
            .Setup(r => r.GetAgentAsync("build"))
            .ReturnsAsync(buildAgent);

        var modelManager = CreateModelManager(
            new SeeingAgentOptions { DefaultModel = "openai/gpt-4o" },
            buildAgent);

        var manager = CreateSessionManager();
        var session = await manager.EnsureSessionAsync("test-session", selectedAgent: "acp-opencode");
        session.SelectedModel = "old-model";
        session.AddMessage(SessionMessage.UserMessage("hello"));
        session.AddMessage(SessionMessage.AssistantMessage("hi"));
        session.Metadata[SessionMetadataKeys.InstructionFingerprints] =
            """{"cwd":"/repo","files":{"/repo/AGENTS.md":"sha256:abc"}}""";
        await manager.SaveAsync(session.Id);

        var service = new GatewaySessionService(manager, registryMock.Object, runtimeMock.Object, modelManager);
        var result = await service.ResetAsync(session.Id);

        result.Should().NotBeNull();
        result!.SessionId.Should().Be("test-session");
        result.Cleared.Should().BeTrue();
        result.MessageCount.Should().Be(0);

        var loaded = manager.Get(session.Id);
        loaded.Should().NotBeNull();
        loaded!.Messages.Should().BeEmpty();
        loaded.Metadata.Should().NotContainKey(SessionMetadataKeys.InstructionFingerprints);
        loaded.SelectedAgent.Should().Be("build");
        loaded.SelectedModel.Should().Be("openai/gpt-4o");
    }

    [Fact]
    public async Task ResetAsync_ShouldReturnNullWhenSessionMissing()
    {
        var registryMock = new Mock<IAgentRegistry>();
        var modelManager = CreateModelManager(new SeeingAgentOptions(), new AgentDefinition { Name = "build" });
        var manager = CreateSessionManager();
        var service = new GatewaySessionService(manager, registryMock.Object, new Mock<IAgentRuntimeManager>().Object, modelManager);

        var result = await service.ResetAsync("missing-session");

        result.Should().BeNull();
    }

    private static IModelManager CreateModelManager(
        SeeingAgentOptions options,
        AgentDefinition agent)
    {
        var catalog = new Mock<IModelConfigManager>();
        catalog.Setup(c => c.GetDefaultModel()).Returns(options.DefaultModel);
        catalog.Setup(c => c.GetModel(It.IsAny<string>())).Returns((string _) => null);
        catalog.Setup(c => c.GetModels()).Returns(new Dictionary<string, ModelConfig>());

        var store = new Mock<IAgentStore>();
        store.Setup(s => s.GetAsync(agent.Name)).ReturnsAsync(agent);

        return new ModelManager(catalog.Object, store.Object);
    }

    private static SessionManager CreateSessionManager()
    {
        var store = new InMemorySessionStore();
        return new SessionManager(
            store: store,
            logger: NullLogger<SessionManager>.Instance);
    }
}

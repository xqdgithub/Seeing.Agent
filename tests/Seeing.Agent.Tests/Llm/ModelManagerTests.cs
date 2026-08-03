using FluentAssertions;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

public class ModelManagerTests
{
    [Fact]
    public void ResolveNativeModel_PrefersRequestOverSessionAgentAndDefault()
    {
        var manager = CreateManager(
            new SeeingAgentOptions { DefaultModel = "openai/gpt-4o" },
            agentName: "build",
            agentModel: "anthropic/claude-sonnet",
            runtime: AgentRuntime.Native);

        var result = manager.ResolveNativeModel(
            requestModelRef: "request-model",
            sessionModelRef: "session-model",
            agentName: "build");

        result.Should().Be("request-model");
    }

    [Fact]
    public void ResolveNativeModel_PrefersAgentModelOverDefault()
    {
        var manager = CreateManager(
            new SeeingAgentOptions { DefaultModel = "openai/gpt-4o" },
            agentName: "build",
            agentModel: "anthropic/claude-sonnet",
            runtime: AgentRuntime.Native);

        var result = manager.ResolveNativeModel(null, null, "build");

        result.Should().Be("anthropic/claude-sonnet");
    }

    [Fact]
    public void ResolveNativeModel_FallsBackToDefaultModel()
    {
        var manager = CreateManager(
            new SeeingAgentOptions { DefaultModel = "openai/gpt-4o" },
            agentName: "build",
            agentModel: null,
            runtime: AgentRuntime.Native);

        var result = manager.ResolveNativeModel(null, null, "build");

        result.Should().Be("openai/gpt-4o");
    }

    [Fact]
    public void ResolveAcpModel_DoesNotFallBackToDefaultModel()
    {
        var manager = CreateManager(
            new SeeingAgentOptions { DefaultModel = "openai/gpt-4o" },
            agentName: "acp-agent",
            agentModel: "anthropic/claude-sonnet",
            runtime: AgentRuntime.AcpPassthrough);

        var result = manager.ResolveAcpModel(null, null);

        result.Should().BeNull();
    }

    [Fact]
    public void ApplyModelToSession_WritesSelectedModelOnly()
    {
        var manager = CreateManager(
            new SeeingAgentOptions(),
            agentName: "build",
            agentModel: null,
            runtime: AgentRuntime.Native);

        var session = SessionData.Create();
        session.SelectedModel = "previous-model";

        var changed = manager.ApplyModelToSession(session, "  openai/gpt-4o  ");

        changed.Should().BeTrue();
        session.SelectedModel.Should().Be("openai/gpt-4o");

        var cleared = manager.ApplyModelToSession(session, "   ");

        cleared.Should().BeTrue();
        session.SelectedModel.Should().BeEmpty();
    }

    [Fact]
    public void SeedSessionModel_Native_WritesResolvedDefault()
    {
        var manager = CreateManager(
            new SeeingAgentOptions { DefaultModel = "openai/gpt-4o" },
            agentName: "build",
            agentModel: null,
            runtime: AgentRuntime.Native);

        var session = SessionData.Create(selectedAgent: "build");
        session.SelectedModel = string.Empty;

        var seeded = manager.SeedSessionModel(session, "build");

        seeded.Should().BeTrue();
        session.SelectedModel.Should().Be("openai/gpt-4o");
    }

    [Fact]
    public void SeedSessionModel_Acp_DoesNotWriteDefaultModel()
    {
        var manager = CreateManager(
            new SeeingAgentOptions { DefaultModel = "openai/gpt-4o" },
            agentName: "acp-agent",
            agentModel: "anthropic/claude-sonnet",
            runtime: AgentRuntime.AcpPassthrough);

        var session = SessionData.Create(selectedAgent: "acp-agent");
        session.SelectedModel = string.Empty;

        var seeded = manager.SeedSessionModel(session, "acp-agent");

        seeded.Should().BeFalse();
        session.SelectedModel.Should().BeEmpty();
    }

    private static IModelManager CreateManager(
        SeeingAgentOptions options,
        string agentName,
        string? agentModel,
        AgentRuntime runtime)
    {
        var store = new Mock<IAgentStore>();
        store
            .Setup(s => s.GetAsync(agentName))
            .ReturnsAsync(new AgentDefinition
            {
                Name = agentName,
                Runtime = runtime,
                Model = ModelReference.Parse(agentModel),
            });

        var catalog = new Mock<IModelConfigManager>();
        catalog.Setup(c => c.GetDefaultModel()).Returns(options.DefaultModel);
        catalog.Setup(c => c.GetModel(It.IsAny<string>())).Returns((string _) => null);
        catalog.Setup(c => c.GetModels()).Returns(new Dictionary<string, ModelConfig>());

        return new ModelManager(catalog.Object, store.Object);
    }
}

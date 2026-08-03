using FluentAssertions;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Gateway;

public class AcpExecutionContextBuilderTests
{
    [Fact]
    public void Resolve_ShouldPreferRequestModelAndMode()
    {
        var modelManager = CreateModelManager(new SeeingAgentOptions { DefaultModel = "anthropic/GLM-5" });
        var resolver = new AgentSelectionResolver(new Mock<IAgentRegistry>().Object);
        var session = new SessionData
        {
            SelectedModel = "session/model",
            SelectedAcpMode = "ask"
        };

        var overrides = AcpExecutionContextBuilder.Resolve(
            modelManager,
            resolver,
            requestModelId: "seeing-coding-plan/GLM-5",
            requestModeId: "build",
            session);

        overrides.ModelId.Should().Be("seeing-coding-plan/GLM-5");
        overrides.ModeId.Should().Be("build");
    }

    [Fact]
    public void ApplyToContext_ShouldWriteAcpMetadataKeys()
    {
        var context = new AgentContext { SessionId = "sess-1" };
        var overrides = new AcpExecutionOverrides("seeing-coding-plan/GLM-5", "build");

        AcpExecutionContextBuilder.ApplyToContext(context, overrides);

        context.Metadata[AgentContextKeys.RequestModelId].Should().Be("seeing-coding-plan/GLM-5");
        context.Metadata[AgentContextKeys.AcpModeId].Should().Be("build");
    }

    [Fact]
    public void ApplyToSession_ShouldPersistModelAndMode()
    {
        var modelManager = CreateModelManager(new SeeingAgentOptions());
        var session = new SessionData();
        var overrides = new AcpExecutionOverrides("seeing-coding-plan/GLM-5", "build");

        AcpExecutionContextBuilder.ApplyToSession(session, overrides, modelManager);

        session.SelectedModel.Should().Be("seeing-coding-plan/GLM-5");
        session.SelectedAcpMode.Should().Be("build");
    }

    private static IModelManager CreateModelManager(SeeingAgentOptions options)
    {
        var catalog = new Mock<IModelConfigManager>();
        catalog.Setup(c => c.GetDefaultModel()).Returns(options.DefaultModel);
        catalog.Setup(c => c.GetModels()).Returns(new Dictionary<string, ModelConfig>());
        return new ModelManager(catalog.Object, new Mock<IAgentStore>().Object);
    }
}

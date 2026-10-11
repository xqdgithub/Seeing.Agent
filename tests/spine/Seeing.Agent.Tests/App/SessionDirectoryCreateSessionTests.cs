using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Hosting;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Xunit;

namespace Seeing.Agent.Tests.App;

/// <summary>
/// SessionDirectory.CreateAsync：默认 Agent / 默认模型种子逻辑（自 ChatOrchestrator 迁出）。
/// </summary>
public class SessionDirectoryCreateSessionTests
{
    [Fact]
    public async Task CreateAsync_ShouldSeedDefaultAgentAndDefaultModel()
    {
        var sessionManager = new SessionManager(logger: NullLogger<SessionManager>.Instance);
        var options = Options.Create(new SeeingAgentOptions
        {
            DefaultAgent = "build",
            DefaultModel = "openai/gpt-4o"
        });

        var registry = new Mock<IAgentRegistry>();
        var runtime = new Mock<IAgentRuntimeManager>();
        runtime.Setup(r => r.GetDefaultAgentNameAsync()).ReturnsAsync("build");
        var buildAgent = new AgentDefinition
        {
            Name = "build",
            Runtime = AgentRuntime.Native
        };
        registry.Setup(r => r.GetAgentAsync("build")).ReturnsAsync(buildAgent);

        var modelManager = CreateModelManager(options.Value, buildAgent);

        var directory = CreateDirectory(sessionManager, registry.Object, runtime.Object, modelManager);

        var session = await directory.CreateAsync(title: "新会话", ct: TestContext.Current.CancellationToken);

        session.SelectedAgent.Should().Be("build");
        session.SelectedModel.Should().Be("openai/gpt-4o");
        session.Title.Should().Be("新会话");
    }

    [Fact]
    public async Task CreateAsync_ForAcpAgent_ShouldNotSeedNativeDefaultModel()
    {
        var sessionManager = new SessionManager(logger: NullLogger<SessionManager>.Instance);
        var options = Options.Create(new SeeingAgentOptions
        {
            DefaultAgent = "acp-cursor",
            DefaultModel = "openai/gpt-4o"
        });

        var registry = new Mock<IAgentRegistry>();
        var runtime = new Mock<IAgentRuntimeManager>();
        runtime.Setup(r => r.GetDefaultAgentNameAsync()).ReturnsAsync("acp-cursor");
        var acpAgent = new AgentDefinition
        {
            Name = "acp-cursor",
            Runtime = AgentRuntime.AcpPassthrough
        };
        registry.Setup(r => r.GetAgentAsync("acp-cursor")).ReturnsAsync(acpAgent);

        var modelManager = CreateModelManager(options.Value, acpAgent);

        var directory = CreateDirectory(sessionManager, registry.Object, runtime.Object, modelManager);

        var session = await directory.CreateAsync(ct: TestContext.Current.CancellationToken);

        session.SelectedAgent.Should().Be("acp-cursor");
        session.SelectedModel.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_WhenAgentHasModel_ShouldPreferAgentModelOverDefault()
    {
        var sessionManager = new SessionManager(logger: NullLogger<SessionManager>.Instance);
        var options = Options.Create(new SeeingAgentOptions
        {
            DefaultAgent = "build",
            DefaultModel = "openai/gpt-4o"
        });

        var registry = new Mock<IAgentRegistry>();
        var runtime = new Mock<IAgentRuntimeManager>();
        runtime.Setup(r => r.GetDefaultAgentNameAsync()).ReturnsAsync("build");
        var buildAgent = new AgentDefinition
        {
            Name = "build",
            Runtime = AgentRuntime.Native,
            Model = new ModelReference { ProviderId = "anthropic", ModelId = "claude-sonnet" }
        };
        registry.Setup(r => r.GetAgentAsync("build")).ReturnsAsync(buildAgent);

        var modelManager = CreateModelManager(options.Value, buildAgent);

        var directory = CreateDirectory(sessionManager, registry.Object, runtime.Object, modelManager);

        var session = await directory.CreateAsync(ct: TestContext.Current.CancellationToken);

        session.SelectedModel.Should().Be("anthropic/claude-sonnet");
    }

    [Fact]
    public async Task CreateAsync_ShouldMaterializeProcessDefaultScenario()
    {
        var sessionManager = new SessionManager(logger: NullLogger<SessionManager>.Instance);
        var options = Options.Create(new SeeingAgentOptions
        {
            DefaultAgent = "build",
            DefaultModel = "openai/gpt-4o",
            Scenario = "code",
        });

        var registry = new Mock<IAgentRegistry>();
        var runtime = new Mock<IAgentRuntimeManager>();
        runtime.Setup(r => r.GetDefaultAgentNameAsync()).ReturnsAsync("build");
        var buildAgent = new AgentDefinition
        {
            Name = "build",
            Runtime = AgentRuntime.Native
        };
        registry.Setup(r => r.GetAgentAsync("build")).ReturnsAsync(buildAgent);

        var modelManager = CreateModelManager(options.Value, buildAgent);
        var workMode = new Mock<IDefaultWorkModeProvider>();
        workMode.Setup(w => w.GetDefaultScenario()).Returns("code");

        var directory = CreateDirectory(sessionManager, registry.Object, runtime.Object, modelManager, workMode.Object);

        var session = await directory.CreateAsync(title: "场景会话", ct: TestContext.Current.CancellationToken);

        session.Scenario.Should().Be("code");
        await sessionManager.SaveAsync(session.Id);
        sessionManager.Get(session.Id)!.Scenario.Should().Be("code");
    }

    private static SessionDirectory CreateDirectory(
        ISessionManager sessionManager,
        IAgentRegistry agentRegistry,
        IAgentRuntimeManager runtimeManager,
        IModelManager modelManager,
        IDefaultWorkModeProvider? defaultWorkMode = null)
    {
        _ = agentRegistry;
        var groupManager = new SessionGroupManager(
            sessionManager,
            new Seeing.Session.Storage.FileSessionGroupStore(
                Path.Combine(Path.GetTempPath(), "seeing-grp-tests", Guid.NewGuid().ToString("N"))),
            new SessionForker(NullLogger<SessionForker>.Instance, sessionManager),
            NullLogger<SessionGroupManager>.Instance);

        return new SessionDirectory(
            sessionManager,
            groupManager,
            new AgentSelectionResolver(runtimeManager),
            modelManager,
            new Mock<ISessionRuntime>().Object,
            NullLogger<SessionDirectory>.Instance,
            defaultWorkMode);
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
        store.Setup(s => s.Get(agent.Name)).Returns(agent);

        return new ModelManager(catalog.Object, store.Object);
    }
}

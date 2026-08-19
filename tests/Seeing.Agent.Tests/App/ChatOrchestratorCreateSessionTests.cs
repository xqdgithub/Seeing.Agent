using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Agents;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.App;
using Seeing.Agent.App.Execution;
using Seeing.Agent.App.Internal;
using Seeing.Agent.Commands;
using Seeing.Agent.Compression;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Xunit;

namespace Seeing.Agent.Tests.App;

public class ChatOrchestratorCreateSessionTests
{
    [Fact]
    public async Task CreateSessionAsync_ShouldSeedDefaultAgentAndDefaultModel()
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

        var orchestrator = CreateOrchestrator(
            sessionManager,
            registry.Object,
            new AgentSelectionResolver(runtime.Object),
            modelManager);

        var session = await orchestrator.CreateSessionAsync(title: "新会话");

        session.SelectedAgent.Should().Be("build");
        session.SelectedModel.Should().Be("openai/gpt-4o");
        session.Title.Should().Be("新会话");
    }

    [Fact]
    public async Task CreateSessionAsync_ForAcpAgent_ShouldNotSeedNativeDefaultModel()
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

        var orchestrator = CreateOrchestrator(
            sessionManager,
            registry.Object,
            new AgentSelectionResolver(runtime.Object),
            modelManager);

        var session = await orchestrator.CreateSessionAsync();

        session.SelectedAgent.Should().Be("acp-cursor");
        session.SelectedModel.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateSessionAsync_WhenAgentHasModel_ShouldPreferAgentModelOverDefault()
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

        var orchestrator = CreateOrchestrator(
            sessionManager,
            registry.Object,
            new AgentSelectionResolver(runtime.Object),
            modelManager);

        var session = await orchestrator.CreateSessionAsync();

        session.SelectedModel.Should().Be("anthropic/claude-sonnet");
    }

    private static ChatOrchestrator CreateOrchestrator(
        ISessionManager sessionManager,
        IAgentRegistry agentRegistry,
        AgentSelectionResolver selectionResolver,
        IModelManager modelManager)
    {
        var executionJobService = new ExecutionJobService(
            serviceProvider: Mock.Of<IServiceProvider>(),
            eventPublisher: Mock.Of<IExecutionEventPublisher>(),
            options: new ExecutionOptions(),
            seeingAgentOptions: Mock.Of<Microsoft.Extensions.Options.IOptionsMonitor<SeeingAgentOptions>>(
                m => m.CurrentValue == new SeeingAgentOptions()),
            logger: NullLogger<ExecutionJobService>.Instance,
            compressionService: new CompressionService(null!, Mock.Of<ISessionManager>(), new CompressionOptions()));

        return new ChatOrchestrator(
            executionJobService: executionJobService,
            sessionManager: sessionManager,
            agentRegistry: agentRegistry,
            workspaceProvider: Mock.Of<IWorkspaceProvider>(),
            executionRouter: Mock.Of<IAgentExecutor>(),
            commandRegistry: Mock.Of<ICommandRegistry>(),
            permissionChannel: Mock.Of<IPermissionChannel>(),
            agentSelectionResolver: selectionResolver,
            modelManager: modelManager,
            executionQueue: new ChatExecutionQueue(),
            runTracker: new ChatRunTracker(),
            logger: NullLogger<ChatOrchestrator>.Instance);
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
}

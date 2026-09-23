using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Compression;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Hosting;
using Seeing.Agent.Hosting.Execution;
using Seeing.Agent.Llm;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App;

public class ChatOrchestratorDeleteSessionTests
{
    [Fact]
    public async Task DeleteSessionAsync_ShouldCancelTargetSessionExecution()
    {
        const string target = "session-target";

        var jobService = new Mock<ExecutionJobService>(
            Mock.Of<IServiceProvider>(),
            Mock.Of<IExecutionEventPublisher>(),
            new ExecutionOptions(),
            Mock.Of<IOptionsMonitor<SeeingAgentOptions>>(m => m.CurrentValue == new SeeingAgentOptions()),
            Mock.Of<IConfigSectionStore>(),
            NullLogger<ExecutionJobService>.Instance,
            new CompactionRunner(
                new CompressionService(null!, Mock.Of<ISessionManager>()),
                Mock.Of<IExecutionEventPublisher>(),
                Mock.Of<ISessionManager>()),
            null!,
            null!);
        jobService
            .Setup(s => s.CancelBySessionAsync(target, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var groupManager = new Mock<ISessionGroupManager>();
        groupManager
            .Setup(g => g.ListChildrenAsync(target, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SessionData>());
        groupManager
            .Setup(g => g.RemoveSessionAsync(target, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        try
        {
            var orchestrator = new ChatOrchestrator(
                executionJobService: jobService.Object,
                sessionManager: Mock.Of<ISessionManager>(),
                groupManager: groupManager.Object,
                agentRegistry: Mock.Of<IAgentRegistry>(),
                workspaceProvider: Mock.Of<IWorkspaceProvider>(),
                executionRouter: Mock.Of<IAgentExecutor>(),
                commandRegistry: Mock.Of<ICommandRegistry>(),
                agentSelectionResolver: new AgentSelectionResolver(Mock.Of<IAgentRuntimeManager>()),
                modelManager: Mock.Of<IModelManager>(),
                executionQueue: new ChatExecutionQueue(),
                runTracker: new ChatRunTracker(),
                logger: NullLogger<ChatOrchestrator>.Instance);

            await orchestrator.DeleteSessionAsync(target, TestContext.Current.CancellationToken);

            jobService.Verify(
                s => s.CancelBySessionAsync(target, It.IsAny<CancellationToken>()),
                Times.Once);
            groupManager.Verify(
                g => g.RemoveSessionAsync(target, It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            jobService.Object.Dispose();
        }
    }
}

using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Chat;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Models;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Commands;
using Seeing.Agent.Core.Compression;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Core.Instructions;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Hosting;
using Seeing.Agent.Hosting.Execution;
using Seeing.Agent.Llm;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Xunit;

namespace Seeing.Agent.Tests.App;

public class ChatOrchestratorReconcileTests
{
    private static ChatOrchestrator CreateOrchestrator(
        ISessionManager sessionManager, IAgentExecutor? executor = null)
    {
        var agentRegistry = new Mock<IAgentRegistry>();
        agentRegistry.Setup(r => r.GetAgentAsync(It.IsAny<string>()))
            .ReturnsAsync(new AgentDefinition { Name = "general", Runtime = AgentRuntime.Native });
        var runtimeManager = new Mock<IAgentRuntimeManager>();
        runtimeManager.Setup(r => r.GetDefaultAgentNameAsync()).ReturnsAsync("general");

        var instructionManager = new Mock<IInstructionManager>();
        instructionManager.Setup(m => m.InjectIfNeededAsync(
                It.IsAny<SessionData>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InstructionInjectResult { Injected = false });

        var modelManager = new Mock<IModelManager>();
        modelManager.Setup(m => m.GetSessionModelRef(It.IsAny<SessionData>())).Returns(string.Empty);
        modelManager.Setup(m => m.ResolveNativeModel(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>()))
            .Returns("m");
        modelManager.Setup(m => m.ResolveAcpModel(It.IsAny<string?>(), It.IsAny<string?>())).Returns((string?)null);

        var services = new ServiceCollection();
        services.AddSingleton(sessionManager);
        services.AddSingleton(instructionManager.Object);
        services.AddSingleton(modelManager.Object);
        services.AddSingleton(agentRegistry.Object);
        services.AddSingleton(executor ?? Mock.Of<IAgentExecutor>());
        services.AddSingleton(new AgentSelectionResolver(runtimeManager.Object));
        services.AddSingleton(Mock.Of<IWorkspaceProvider>(w =>
            w.ProjectSeeingDirectory == Path.Combine("workspace-root", ".seeing")));
        services.AddSingleton(Mock.Of<IExecutionWorld>(w => w.Cwd == "workspace-root"));
        services.AddSingleton(Mock.Of<ICommandRegistry>());
        var provider = services.BuildServiceProvider();

        var configStore = new Mock<IConfigSectionStore>();
        configStore.Setup(s => s.GetSection<TokenBudgetAutoCompactionPeek>("TokenBudget"))
            .Returns(new TokenBudgetAutoCompactionPeek { AutoCompactionEnabled = false });

        var executionJobService = new ExecutionJobService(
            serviceProvider: provider,
            eventPublisher: Mock.Of<IExecutionEventPublisher>(),
            options: new ExecutionOptions(),
            seeingAgentOptions: Mock.Of<IOptionsMonitor<SeeingAgentOptions>>(
                m => m.CurrentValue == new SeeingAgentOptions()),
            configStore: configStore.Object,
            logger: NullLogger<ExecutionJobService>.Instance,
            compactionRunner: new CompactionRunner(
                new CompressionService(null!, Mock.Of<ISessionManager>()),
                Mock.Of<IExecutionEventPublisher>(),
                Mock.Of<ISessionManager>()));

        return new ChatOrchestrator(
            executionJobService: executionJobService,
            sessionManager: sessionManager,
            groupManager: new SessionGroupManager(
                sessionManager,
                new Seeing.Session.Storage.FileSessionGroupStore(
                    Path.Combine(Path.GetTempPath(), "seeing-grp-tests", Guid.NewGuid().ToString("N"))),
                new SessionForker(NullLogger<SessionForker>.Instance, sessionManager),
                NullLogger<SessionGroupManager>.Instance),
            agentRegistry: agentRegistry.Object,
            workspaceProvider: Mock.Of<IWorkspaceProvider>(),
            executionRouter: executor ?? Mock.Of<IAgentExecutor>(),
            commandRegistry: Mock.Of<ICommandRegistry>(),
            agentSelectionResolver: new AgentSelectionResolver(runtimeManager.Object),
            modelManager: modelManager.Object,
            logger: NullLogger<ChatOrchestrator>.Instance);
    }

    private static async Task<SessionData> CreateSessionWithTaskAsync(SessionManager sessionManager, string status)
    {
        var session = await sessionManager.EnsureSessionAsync("s-reconcile");
        var msg = SessionMessage.AssistantMessage("thinking");
        msg.ToolCalls = ImmutableList.Create(new SessionToolCall
        {
            Id = "t1",
            Name = "task",
            Status = status,
            TaskId = "child-orphan"
        });
        session.AddMessage(msg);
        return session;
    }

    [Fact]
    public async Task ReconcileIncompleteTasksAsync_NoActiveExecution_ShouldMarkCancelled()
    {
        var sessionManager = new SessionManager(logger: NullLogger<SessionManager>.Instance);
        var session = await CreateSessionWithTaskAsync(sessionManager, "running");

        var orchestrator = CreateOrchestrator(sessionManager);

        var count = await orchestrator.ReconcileIncompleteTasksAsync(
            session.Id, TestContext.Current.CancellationToken);

        count.Should().Be(1);
        var tc = sessionManager.Get(session.Id)!.Messages[0].ToolCalls![0];
        tc.Status.Should().Be("cancelled");
        tc.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ReconcileIncompleteTasksAsync_CompletedTask_ShouldNotTouch()
    {
        var sessionManager = new SessionManager(logger: NullLogger<SessionManager>.Instance);
        var session = await CreateSessionWithTaskAsync(sessionManager, "success");

        var orchestrator = CreateOrchestrator(sessionManager);

        var count = await orchestrator.ReconcileIncompleteTasksAsync(
            session.Id, TestContext.Current.CancellationToken);

        count.Should().Be(0);
        sessionManager.Get(session.Id)!.Messages[0].ToolCalls![0].Status.Should().Be("success");
    }

    [Fact]
    public async Task ReconcileIncompleteTasksAsync_SessionStillExecuting_ShouldSkip()
    {
        // 会话自身仍在执行（前台 task 在途、TaskId 未回填）：不得误判为孤儿取消。
        var sessionManager = new SessionManager(logger: NullLogger<SessionManager>.Instance);
        var session = await CreateSessionWithTaskAsync(sessionManager, "running");

        var executor = new Mock<IAgentExecutor>();
        executor.Setup(e => e.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Returns((AgentDefinition _, IReadOnlyList<ChatMessage> _, AgentContext _, CancellationToken ct)
                => BlockingStream(ct));

        var orchestrator = CreateOrchestrator(sessionManager, executor.Object);

        var result = await orchestrator.SubmitAsync(
            session.Id,
            ChatInput.FromText("hi"),
            new ChatOptions { AgentId = "general", SkipUserMessagePersist = true, SkipInstructionInject = true });
        result.Success.Should().BeTrue();

        await WaitUntilAsync(() =>
            orchestrator.GetOverview(session.Id).CurrentExecution?.Status == ExecutionStatus.Running);

        var count = await orchestrator.ReconcileIncompleteTasksAsync(
            session.Id, TestContext.Current.CancellationToken);

        count.Should().Be(0);
        sessionManager.Get(session.Id)!.Messages[0].ToolCalls![0].Status.Should().Be("running");

        await orchestrator.CancelBySessionAsync(session.Id);
    }

    private static async IAsyncEnumerable<IMessageEvent> BlockingStream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        await Task.Delay(Timeout.Infinite, token);
        yield break;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("WaitUntil condition not met");
            await Task.Delay(20);
        }
    }
}

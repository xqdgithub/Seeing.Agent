using Seeing.Agent.Abstractions.Agents;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Models;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Core;
using Seeing.Agent.Hosting.Execution;
using Seeing.Agent.Core.Compression;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Instructions;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Xunit;

namespace Seeing.Agent.Tests.App;

/// <summary>
/// ExecutionRunner.RunOnceAsync：执行前职责（指令注入、cwd 更新）。
/// 入站 user 消息已由 ISessionRuntime.DeliverAsync 落盘，本轮不再落盘（SkipUserMessagePersist 语义恒真）。
/// </summary>
public class ExecutionRunnerInstructionTests
{
    [Fact]
    public async Task RunOnce_ShouldInjectInstructionsAndUpdateWorkingDirectory()
    {
        var session = SessionData.Create();
        session.WorkingDirectory = "old-cwd";
        var instructionManager = CreateInstructionManager(session);
        var runner = CreateRunner(session, instructionManager.Object, "workspace-root");

        var record = await runner.RunOnceAsync(
            NewRecord(session.Id, workingDirectory: "resolved-cwd"),
            NewBatch(),
            TestContext.Current.CancellationToken);

        record.Status.Should().BeOneOf(ExecutionStatus.Completed, ExecutionStatus.Failed);
        session.WorkingDirectory.Should().Be("resolved-cwd");
        session.Messages.Select(message => message.Content)
            .Should().Contain("injected instructions");
        instructionManager.Verify(manager => manager.InjectIfNeededAsync(
            session, "resolved-cwd", "workspace-root", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunOnce_WhenInstructionInjectionIsSkipped_ShouldNotInject()
    {
        var session = SessionData.Create();
        var instructionManager = CreateInstructionManager(session);
        var runner = CreateRunner(session, instructionManager.Object, "workspace-root");

        await runner.RunOnceAsync(
            NewRecord(session.Id, skipInstructionInject: true),
            NewBatch(),
            TestContext.Current.CancellationToken);

        session.Messages.Should().NotContain(message => message.Content == "injected instructions");
        instructionManager.Verify(manager => manager.InjectIfNeededAsync(
            It.IsAny<SessionData>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunOnce_WhenInstructionInjectionFails_ShouldContinueExecution()
    {
        var session = SessionData.Create();
        var instructionManager = new Mock<IInstructionManager>();
        instructionManager
            .Setup(manager => manager.InjectIfNeededAsync(
                session, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("injection failed"));
        var runner = CreateRunner(session, instructionManager.Object, "workspace-root");

        var record = await runner.RunOnceAsync(
            NewRecord(session.Id),
            NewBatch(),
            TestContext.Current.CancellationToken);

        // 注入失败仅告警，不中断本轮：执行体仍被调用并产出终态
        record.Status.Should().NotBe(ExecutionStatus.Pending);
    }

    [Fact]
    public async Task RunOnce_ShouldPublish_ExecutionCompleteEvent()
    {
        var session = SessionData.Create();
        var instructionManager = CreateInstructionManager(session);
        var published = new List<IMessageEvent>();
        var publisher = new Mock<IExecutionEventPublisher>();
        publisher
            .Setup(p => p.Publish(It.IsAny<string>(), It.IsAny<IMessageEvent>()))
            .Callback<string, IMessageEvent>((_, evt) => published.Add(evt));

        var runner = CreateRunner(session, instructionManager.Object, "workspace-root", publisher.Object);

        await runner.RunOnceAsync(
            NewRecord(session.Id),
            NewBatch(),
            TestContext.Current.CancellationToken);

        // 每轮执行恰发布一次 ExecutionCompleteEvent（UI/TUI 据此结束「执行中」态）
        var complete = published.OfType<ExecutionCompleteEvent>().ToList();
        complete.Should().ContainSingle();
        complete[0].ExecutionId.Should().Be("exec-1");
    }

    private static ExecutionRecord NewRecord(
        string sessionId,
        string? workingDirectory = null,
        bool skipInstructionInject = false) => new()
    {
        ExecutionId = "exec-1",
        SessionId = sessionId,
        Status = ExecutionStatus.Running,
        Input = new ChatInput { Text = "real user message" },
        Options = new ChatOptions
        {
            AgentId = "general",
            WorkingDirectory = workingDirectory,
            SkipInstructionInject = skipInstructionInject
        }
    };

    private static InboxBatch NewBatch() =>
        new(new List<SessionMessage> { SessionMessage.UserMessage("real user message") }, HasUserInput: false, FirstUserText: null);

    private static Mock<IInstructionManager> CreateInstructionManager(SessionData expectedSession)
    {
        var instructionManager = new Mock<IInstructionManager>();
        instructionManager
            .Setup(manager => manager.InjectIfNeededAsync(
                expectedSession, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<SessionData, string, string, CancellationToken>((session, _, _, _) =>
                session.AddMessage(SessionMessage.UserMessage("injected instructions")))
            .ReturnsAsync(new InstructionInjectResult { Injected = true });
        return instructionManager;
    }

    private static ExecutionRunner CreateRunner(
        SessionData session,
        IInstructionManager instructionManager,
        string workspaceRoot,
        IExecutionEventPublisher? publisher = null)
    {
        var sessionManager = new Mock<ISessionManager>();
        sessionManager
            .Setup(manager => manager.EnsureSessionAsync(session.Id, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(session);
        sessionManager.Setup(manager => manager.Get(It.IsAny<string>())).Returns(session);
        sessionManager.Setup(manager => manager.SaveAsync(session.Id)).Returns(Task.CompletedTask);
        sessionManager.Setup(manager => manager.FlushAsync(session.Id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var executor = new Mock<IAgentExecutor>();
        executor.Setup(e => e.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Returns(EmptyStream());

        var agentRegistry = new Mock<IAgentRegistry>();
        agentRegistry.Setup(r => r.GetAgentAsync(It.IsAny<string>()))
            .ReturnsAsync(new AgentDefinition { Name = "general", Runtime = AgentRuntime.Native });

        var services = new ServiceCollection();
        services.AddSingleton(sessionManager.Object);
        services.AddSingleton(CreateModelManager());
        services.AddSingleton(instructionManager);
        services.AddSingleton(executor.Object);
        services.AddSingleton(agentRegistry.Object);
        services.AddSingleton(new AgentSelectionResolver(Mock.Of<IAgentRuntimeManager>()));
        services.AddSingleton(Mock.Of<IWorkspaceProvider>(
            workspace => workspace.ProjectSeeingDirectory == Path.Combine(workspaceRoot, ".seeing")));
        services.AddSingleton(Mock.Of<IExecutionWorld>(world => world.Cwd == workspaceRoot));
        services.AddSingleton(Mock.Of<ICommandRegistry>());
        var provider = services.BuildServiceProvider();

        var configStore = new Mock<IConfigSectionStore>();
        configStore.Setup(s => s.GetSection<TokenBudgetAutoCompactionPeek>("TokenBudget"))
            .Returns(new TokenBudgetAutoCompactionPeek { AutoCompactionEnabled = false });

        return new ExecutionRunner(
            provider,
            publisher ?? Mock.Of<IExecutionEventPublisher>(),
            new ExecutionOptions(),
            Mock.Of<IOptionsMonitor<SeeingAgentOptions>>(
                monitor => monitor.CurrentValue == new SeeingAgentOptions()),
            configStore.Object,
            new CompactionRunner(
                new CompressionService(null!, Mock.Of<ISessionManager>()),
                Mock.Of<IExecutionEventPublisher>(),
                Mock.Of<ISessionManager>()),
            NullLogger<ExecutionRunner>.Instance);
    }

    private static IModelManager CreateModelManager()
    {
        var catalog = new Mock<IModelConfigManager>();
        catalog.Setup(manager => manager.GetModel(It.IsAny<string>())).Returns((string _) => null);
        catalog.Setup(manager => manager.GetModels()).Returns(new Dictionary<string, ModelConfig>());

        var store = new Mock<IAgentStore>();
        store.Setup(manager => manager.Get(It.IsAny<string>())).Returns(new AgentDefinition
        {
            Name = "build",
            Runtime = AgentRuntime.Native
        });

        return new ModelManager(catalog.Object, store.Object);
    }

    private static async IAsyncEnumerable<IMessageEvent> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }
}

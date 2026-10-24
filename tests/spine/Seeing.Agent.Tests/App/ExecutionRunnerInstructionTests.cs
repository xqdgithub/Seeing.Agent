using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Models;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Compression;
using Seeing.Agent.Core.Context.Sources;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Hosting.Execution;
using Seeing.Agent.Llm;
using Seeing.Agent.Tests.Context;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App;

/// <summary>
/// ExecutionRunner.RunOnceAsync：执行前职责（cwd 更新）与上下文装配（指令源经装配器，不再直连 IInstructionManager）。
/// 入站 user 消息已由 ISessionRuntime.DeliverAsync 落盘，本轮不再落盘（SkipUserMessagePersist 语义恒真）。
/// </summary>
public class ExecutionRunnerInstructionTests
{
    [Fact]
    public async Task RunOnce_ShouldAssembleInstructionsAndUpdateWorkingDirectory()
    {
        var session = SessionData.Create();
        session.WorkingDirectory = "old-cwd";

        var assembler = new RecordingContextAssembler
        {
            OnAssemble = (target, mode) =>
            {
                // 真实装配器在 Persist 模式把初始/增量消息写入会话；此处模拟同一事实
                if (mode == ContextAssemblyMode.Persist)
                    target.AddMessage(SessionMessage.UserMessage("injected instructions"));
            }
        };

        var runner = CreateRunner(session, assembler, "workspace-root");

        var record = await runner.RunOnceAsync(
            NewRecord(session.Id, workingDirectory: "resolved-cwd"),
            NewBatch(),
            TestContext.Current.CancellationToken);

        record.Status.Should().BeOneOf(ExecutionStatus.Completed, ExecutionStatus.Failed);
        session.WorkingDirectory.Should().Be("resolved-cwd");
        session.Messages.Select(message => message.Content)
            .Should().Contain("injected instructions");

        assembler.Calls.Should().ContainSingle();
        assembler.Calls[0].Mode.Should().Be(ContextAssemblyMode.Persist);
        assembler.ObservedCwd.Should().Be("resolved-cwd", "装配器必须看到本轮执行的会话 cwd");
        assembler.ObservedWorkspaceRoot.Should().Be("workspace-root");
    }

    [Fact]
    public async Task RunOnce_WhenInstructionInjectionIsSkipped_ShouldSkipProjectInstructionsSource()
    {
        var session = SessionData.Create();
        var assembler = new RecordingContextAssembler();
        var runner = CreateRunner(session, assembler, "workspace-root");

        await runner.RunOnceAsync(
            NewRecord(session.Id, skipInstructionInject: true),
            NewBatch(),
            TestContext.Current.CancellationToken);

        // 跳过 = 把「指令源」从本轮装配中排除（其余源照常），而不是整轮不装配
        assembler.Calls.Should().ContainSingle();
        assembler.Calls[0].Skip.Should().NotBeNull();
        assembler.Calls[0].Skip!.Should().ContainSingle();
        assembler.Calls[0].Skip!.Should().Contain(ProjectInstructionsContextSource.SourceKey);
        session.Messages.Should().NotContain(message => message.Content == "injected instructions");
    }

    [Fact]
    public async Task RunOnce_WhenAssemblyFails_ShouldContinueExecution()
    {
        var session = SessionData.Create();
        var runner = CreateRunner(session, new ThrowingContextAssembler(), "workspace-root");

        var record = await runner.RunOnceAsync(
            NewRecord(session.Id),
            NewBatch(),
            TestContext.Current.CancellationToken);

        // 装配失败仅告警，不中断本轮：执行体仍被调用并产出终态
        record.Status.Should().NotBe(ExecutionStatus.Pending);
        record.Status.Should().NotBe(ExecutionStatus.Failed);
    }

    [Fact]
    public async Task RunOnce_ShouldPublish_ExecutionCompleteEvent()
    {
        var session = SessionData.Create();
        var assembler = new RecordingContextAssembler();
        var published = new List<IMessageEvent>();
        var publisher = new Mock<IExecutionEventPublisher>();
        publisher
            .Setup(p => p.Publish(It.IsAny<string>(), It.IsAny<IMessageEvent>()))
            .Callback<string, IMessageEvent>((_, evt) => published.Add(evt));

        var runner = CreateRunner(session, assembler, "workspace-root", publisher.Object);

        await runner.RunOnceAsync(
            NewRecord(session.Id),
            NewBatch(),
            TestContext.Current.CancellationToken);

        // 每轮执行恰发布一次 ExecutionCompleteEvent（UI/TUI 据此结束「执行中」态）
        var complete = published.OfType<ExecutionCompleteEvent>().ToList();
        complete.Should().ContainSingle();
        complete[0].ExecutionId.Should().Be("exec-1");
    }

    /// <summary>装配必然失败的假实现（验证失败仅告警）。</summary>
    private sealed class ThrowingContextAssembler : IContextAssembler
    {
        public Task<ContextAssemblyResult> EnsureFreshAsync(
            SessionData session,
            ContextAssemblyMode mode,
            string? insertBeforeMessageId,
            IReadOnlySet<ContextSourceKey>? skip,
            CancellationToken ct = default) =>
            Task.FromException<ContextAssemblyResult>(new InvalidOperationException("assembly failed"));

        public string BuildSystemBaseline(SessionData session) => string.Empty;
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

    private static ExecutionRunner CreateRunner(
        SessionData session,
        IContextAssembler assembler,
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
        services.AddSingleton(Mock.Of<IModelManager>());
        services.AddSingleton(executor.Object);
        services.AddSingleton(agentRegistry.Object);
        services.AddSingleton(new AgentSelectionResolver(Mock.Of<IAgentRuntimeManager>()));
        services.AddSingleton(Mock.Of<IWorkspaceProvider>(
            workspace => workspace.ProjectSeeingDirectory == Path.Combine(workspaceRoot, ".seeing")));
        services.AddSingleton(Mock.Of<IExecutionWorld>(world => world.Cwd == workspaceRoot));
        services.AddSingleton(Mock.Of<ICommandRegistry>());
        services.AddSingleton<ISessionToolExposureWriter>(new SessionToolExposureWriter(sessionManager.Object));
        services.AddStubContextPipeline(assembler);
        var provider = services.BuildServiceProvider();

        var configStore = new Mock<IConfigSectionStore>();
        configStore.Setup(s => s.GetSection<TokenBudgetAutoCompactionPeek>("TokenBudget"))
            .Returns(new TokenBudgetAutoCompactionPeek { AutoCompactionEnabled = false });

        return new ExecutionRunner(
            provider,
            publisher ?? Mock.Of<IExecutionEventPublisher>(),
            new ExecutionOptions(),
            configStore.Object,
            new CompactionRunner(
                new CompressionService(null!, Mock.Of<ISessionManager>()),
                Mock.Of<IExecutionEventPublisher>(),
                Mock.Of<ISessionManager>()),
            NullLogger<ExecutionRunner>.Instance,
            new StubSessionToolSchemaResolver());
    }

    private static async IAsyncEnumerable<IMessageEvent> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }
}

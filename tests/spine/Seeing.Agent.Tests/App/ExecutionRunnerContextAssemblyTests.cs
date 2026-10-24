using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz;
using Quartz.Impl;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Models;
using Seeing.Agent.Abstractions.Reminders;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Compression;
using Seeing.Agent.Core.Context;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Hosting.Execution;
using Seeing.Agent.Llm;
using Seeing.Agent.Scheduler;
using Seeing.Agent.Scheduler.Jobs;
using Seeing.Agent.Tests.Context;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App;

/// <summary>
/// ExecutionRunner 上下文装配接线：装配点（命令之后、历史之前）、执行级快照钉住、
/// 短路命令与结算失败不装配、Scheduler/Heartbeat 旁路只读。
/// </summary>
public class ExecutionRunnerContextAssemblyTests
{
    [Fact]
    public async Task Assembly_ShouldRunAfterCommands_BeforeHistory()
    {
        // Arrange：继续类命令 → 命令处理完成后必须装配，且装配插入的消息要进入本轮历史
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.UserMessage("real user message"));
        var firstUserMessageId = session.Messages[0].Id;

        var steps = new List<string>();
        var assembler = new RecordingContextAssembler
        {
            Baseline = "SYSTEM-BASELINE",
            // 装配插入的叙述消息必须被随后构建的历史看到（证明装配在 BuildHistory 之前）
            OnAssemble = (target, _) =>
            {
                steps.Add("assemble");
                target.AddMessage(SessionMessage.UserMessage("<context-update source=\"fake\">narrated</context-update>"));
            }
        };

        var command = CreateCommand("cont", steps);
        var commandRegistry = CreateCommandRegistry("cont", command);

        IReadOnlyList<ChatMessage>? history = null;
        var executor = new Mock<IAgentExecutor>();
        executor
            .Setup(e => e.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Callback<AgentDefinition, IReadOnlyList<ChatMessage>, AgentContext, CancellationToken>(
                (_, messages, _, _) =>
                {
                    steps.Add("execute");
                    history = messages;
                })
            .Returns(EmptyStream());

        var runner = CreateRunner(session, executor.Object, assembler, commandRegistry.Object);

        // Act
        await runner.RunOnceAsync(
            NewRecord(session.Id, "/cont"), NewBatch(firstUserMessageId), TestContext.Current.CancellationToken);

        // Assert：顺序为 命令 → 装配 → 执行
        steps.Should().ContainInOrder("command", "assemble", "execute");
        assembler.Calls.Should().ContainSingle();
        assembler.Calls[0].Mode.Should().Be(ContextAssemblyMode.Persist);
        assembler.Calls[0].InsertBeforeMessageId.Should().Be(firstUserMessageId);
        assembler.Calls[0].Skip.Should().BeNull("未跳过指令注入时不得跳过任何源");

        history.Should().NotBeNull();
        history!.Should().Contain(message =>
            message.Content != null && message.Content.Contains("narrated", StringComparison.Ordinal),
            "装配发生在历史构建之前，叙述消息必须进入本轮历史");
    }

    [Fact]
    public async Task Assembly_ShouldPassPinnedSnapshotToTools()
    {
        // Arrange：执行级钉住 = 装配器与工具看到同一个快照对象
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.UserMessage("real user message"));
        var snapshot = CreateSnapshot(version: 7);

        var catalog = new Mock<ISkillCatalog>();
        catalog.Setup(c => c.Current).Returns(snapshot);
        catalog.Setup(c => c.EnsureFreshAsync(It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);

        AgentContext? capturedContext = null;
        var executor = new Mock<IAgentExecutor>();
        executor
            .Setup(e => e.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Callback<AgentDefinition, IReadOnlyList<ChatMessage>, AgentContext, CancellationToken>(
                (_, _, context, _) => capturedContext = context)
            .Returns(EmptyStream());

        var assembler = new RecordingContextAssembler { Baseline = "SYSTEM-BASELINE" };
        var runner = CreateRunner(session, executor.Object, assembler,
            configureServices: services => services.AddSingleton(catalog.Object));

        // Act
        await runner.RunOnceAsync(
            NewRecord(session.Id), NewBatch(), TestContext.Current.CancellationToken);

        // Assert：装配器在执行作用域里看到的是同一个快照，AgentContext 携带的也是它
        assembler.ObservedSnapshot.Should().BeSameAs(snapshot);
        capturedContext.Should().NotBeNull();
        capturedContext!.SkillSnapshot.Should().BeSameAs(snapshot);
        capturedContext.ContextBaseline.Should().Be("SYSTEM-BASELINE");
    }

    [Fact]
    public async Task ShortCircuitCommand_ShouldNotAssemble()
    {
        // Arrange：/compact 这类短路命令不执行 Agent，也就不装配、不叙述
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.UserMessage("real user message"));

        var command = new Mock<ICommand>();
        command.Setup(c => c.Metadata)
            .Returns(CommandMetadata.Simple("compact", "压缩会话", usage: "/compact", category: CommandCategory.Basic));
        command.Setup(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.Ok("Compacted", shouldContinue: false, needsRefresh: true));

        var commandRegistry = CreateCommandRegistry("compact", command);

        var assembler = new RecordingContextAssembler();
        var executor = new Mock<IAgentExecutor>();
        executor
            .Setup(e => e.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Returns(EmptyStream());

        var runner = CreateRunner(session, executor.Object, assembler, commandRegistry.Object);

        // Act
        await runner.RunOnceAsync(
            NewRecord(session.Id, "/compact"), NewBatch(), TestContext.Current.CancellationToken);

        // Assert
        assembler.Calls.Should().BeEmpty("本轮未执行 Agent，不得宣称已告知模型");
        executor.Verify(e => e.ExecuteAsync(
            It.IsAny<AgentDefinition>(),
            It.IsAny<IReadOnlyList<ChatMessage>>(),
            It.IsAny<AgentContext>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SettlementFailure_ShouldNotNarrate()
    {
        // 行为变更：结算失败 → 执行没有发生 → 不装配、不叙述（旧实现此时已写入指令消息）
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.UserMessage("real user message"));
        var messageCountBefore = session.Messages.Count;

        var assembler = new RecordingContextAssembler();
        var executor = new Mock<IAgentExecutor>();
        var runner = CreateRunner(
            session,
            executor.Object,
            assembler,
            schemaResolver: new ThrowingSchemaResolver());

        // Act
        var record = await runner.RunOnceAsync(
            NewRecord(session.Id), NewBatch(), TestContext.Current.CancellationToken);

        // Assert
        record.Status.Should().Be(ExecutionStatus.Failed);
        assembler.Calls.Should().BeEmpty();
        session.Messages.Should().HaveCount(messageCountBefore, "结算失败不得留下叙述消息");
        session.Metadata.Should().NotContainKey(SessionMetadataKeys.ContextBaseline);
    }

    [Fact]
    public async Task SchedulerBypass_ShouldUseReadOnlyMode()
    {
        // Arrange：Scheduler 旁路只读装配：不推进状态、不保存会话
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.UserMessage("cron user message"));
        var messageCountBefore = session.Messages.Count;
        var snapshot = CreateSnapshot(version: 3);

        var catalog = new Mock<ISkillCatalog>();
        catalog.Setup(c => c.Current).Returns(snapshot);
        catalog.Setup(c => c.EnsureFreshAsync(It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);

        var assembler = new RecordingContextAssembler { Baseline = "SYSTEM-BASELINE" };

        AgentContext? capturedContext = null;
        var executor = new Mock<IAgentExecutor>();
        executor
            .Setup(e => e.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Callback<AgentDefinition, IReadOnlyList<ChatMessage>, AgentContext, CancellationToken>(
                (_, _, context, _) => capturedContext = context)
            .Returns(() => OneEvent(new StreamCompleteEvent
            {
                SessionId = "s-cron",
                Message = new ChatMessage { Role = ChatRole.Assistant, Content = "done" }
            }));

        var sessionManager = new Mock<ISessionManager>();
        sessionManager
            .Setup(m => m.TryGetOrLoadAsync("s-cron", It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        var provider = CreateBypassServices(sessionManager.Object, catalog.Object, assembler);
        var job = CreateAgentJob(executor.Object, provider);

        // Act
        await job.Execute(CreateJobContextMock().Object);

        // Assert
        assembler.Calls.Should().ContainSingle();
        assembler.Calls[0].Mode.Should().Be(ContextAssemblyMode.ReadOnly);
        assembler.Calls[0].InsertBeforeMessageId.Should().BeNull();
        assembler.Calls[0].Skip.Should().BeNull();
        assembler.ObservedSnapshot.Should().BeSameAs(snapshot, "旁路必须先填充执行作用域再装配");

        capturedContext.Should().NotBeNull();
        capturedContext!.ContextBaseline.Should().Be("SYSTEM-BASELINE", "旁路必须使用只读装配返回的当前值渲染");
        capturedContext.SkillSnapshot.Should().BeSameAs(snapshot);

        session.Messages.Should().HaveCount(messageCountBefore, "只读装配不得插入增量消息");
        session.Metadata.Should().NotContainKey(SessionMetadataKeys.ContextBaseline, "只读装配不得推进状态");
        sessionManager.Verify(m => m.SaveAsync(It.IsAny<string>()), Times.Never, "旁路不得保存会话");
    }

    [Fact]
    public async Task HeartbeatBypass_ShouldNotEmitDeltaMessage()
    {
        // Arrange
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.UserMessage("heartbeat user message"));
        var messageCountBefore = session.Messages.Count;

        var snapshot = CreateSnapshot(version: 5);
        var catalog = new Mock<ISkillCatalog>();
        catalog.Setup(c => c.Current).Returns(snapshot);
        catalog.Setup(c => c.EnsureFreshAsync(It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);

        var assembler = new RecordingContextAssembler { Baseline = "HEARTBEAT-BASELINE" };

        AgentContext? capturedContext = null;
        var executor = new Mock<IAgentExecutor>();
        executor
            .Setup(e => e.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Callback<AgentDefinition, IReadOnlyList<ChatMessage>, AgentContext, CancellationToken>(
                (_, _, context, _) => capturedContext = context)
            .Returns(() => OneEvent(new StreamCompleteEvent
            {
                SessionId = "s-hb",
                Message = new ChatMessage { Role = ChatRole.Assistant, Content = "ok" }
            }));

        var sessionManager = new Mock<ISessionManager>();
        sessionManager
            .Setup(m => m.TryGetOrLoadAsync("s-hb", It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        var provider = CreateBypassServices(sessionManager.Object, catalog.Object, assembler);
        var job = CreateHeartbeatJob(executor.Object, provider, sessionManager.Object);

        // Act
        await job.Execute(CreateHeartbeatContextMock().Object);

        // Assert
        assembler.Calls.Should().ContainSingle();
        assembler.Calls[0].Mode.Should().Be(ContextAssemblyMode.ReadOnly);
        assembler.ObservedSnapshot.Should().BeSameAs(snapshot);
        capturedContext.Should().NotBeNull();
        capturedContext!.ContextBaseline.Should().Be("HEARTBEAT-BASELINE");

        session.Messages.Should().HaveCount(messageCountBefore);
        session.Messages.Should().NotContain(message =>
            message.Content != null && message.Content.Contains("context-update", StringComparison.Ordinal),
            "只读旁路不得产生增量叙述消息");
        sessionManager.Verify(m => m.SaveAsync(It.IsAny<string>()), Times.Never);
    }

    // ===== 假实现 =====

    /// <summary>注定失败的结算：模拟工具视图结算阶段抛错。</summary>
    private sealed class ThrowingSchemaResolver : ISessionToolSchemaResolver
    {
        public Task<SessionToolSchemaResolution> ResolveAsync(
            string sessionId, AgentDefinition agent, CancellationToken cancellationToken = default) =>
            Task.FromException<SessionToolSchemaResolution>(new InvalidOperationException("settlement failed"));

        public Task<SessionToolSchemaResolution> ResolveAsync(
            SessionData session, AgentDefinition agent, CancellationToken cancellationToken = default) =>
            Task.FromException<SessionToolSchemaResolution>(new InvalidOperationException("settlement failed"));
    }

    // ===== ExecutionRunner 装配脚手架 =====

    private static ExecutionRunner CreateRunner(
        SessionData session,
        IAgentExecutor executor,
        IContextAssembler? assembler = null,
        ICommandRegistry? commandRegistry = null,
        ISessionToolSchemaResolver? schemaResolver = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var sessionManager = new Mock<ISessionManager>();
        sessionManager
            .Setup(m => m.EnsureSessionAsync(session.Id, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(session);
        sessionManager.Setup(m => m.Get(It.IsAny<string>())).Returns(session);
        sessionManager.Setup(m => m.SaveAsync(session.Id)).Returns(Task.CompletedTask);
        sessionManager.Setup(m => m.FlushAsync(session.Id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var agentRegistry = new Mock<IAgentRegistry>();
        agentRegistry
            .Setup(r => r.GetAgentAsync(It.IsAny<string>()))
            .ReturnsAsync(new AgentDefinition { Name = "general", Runtime = AgentRuntime.Native });

        var services = new ServiceCollection();
        services.AddSingleton(sessionManager.Object);
        services.AddSingleton(Mock.Of<IModelManager>());
        services.AddSingleton(executor);
        services.AddSingleton(agentRegistry.Object);
        services.AddSingleton(new AgentSelectionResolver(Mock.Of<IAgentRuntimeManager>()));
        services.AddSingleton(Mock.Of<IWorkspaceProvider>(
            workspace => workspace.ProjectSeeingDirectory == Path.Combine("workspace-root", ".seeing")));
        services.AddSingleton(Mock.Of<IExecutionWorld>(world => world.Cwd == "workspace-root"));
        services.AddSingleton(commandRegistry ?? Mock.Of<ICommandRegistry>());
        services.AddSingleton<ISessionToolExposureWriter>(new SessionToolExposureWriter(sessionManager.Object));

        // 执行级上下文管道：装配器与作用域同生命周期（生产同形）
        services.AddStubContextPipeline(assembler);

        configureServices?.Invoke(services);
        var provider = services.BuildServiceProvider();

        var configStore = new Mock<IConfigSectionStore>();
        configStore
            .Setup(s => s.GetSection<TokenBudgetAutoCompactionPeek>("TokenBudget"))
            .Returns(new TokenBudgetAutoCompactionPeek { AutoCompactionEnabled = false });

        return new ExecutionRunner(
            provider,
            Mock.Of<IExecutionEventPublisher>(),
            new ExecutionOptions(),
            configStore.Object,
            new CompactionRunner(
                new CompressionService(null!, sessionManager.Object),
                Mock.Of<IExecutionEventPublisher>(),
                sessionManager.Object),
            NullLogger<ExecutionRunner>.Instance,
            schemaResolver ?? new StubSessionToolSchemaResolver());
    }

    private static Mock<ICommand> CreateCommand(string name, List<string> steps)
    {
        var command = new Mock<ICommand>();
        command.Setup(c => c.Metadata)
            .Returns(CommandMetadata.Simple(name, $"{name} 命令", category: CommandCategory.Other));
        command.Setup(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => steps.Add("command"))
            .ReturnsAsync(CommandResult.Ok("continued", shouldContinue: true));
        return command;
    }

    private static Mock<ICommandRegistry> CreateCommandRegistry(string name, Mock<ICommand> command)
    {
        var registry = new Mock<ICommandRegistry>();
        registry.Setup(r => r.GetCommand(name, AgentRuntime.Native)).Returns(command.Object);
        return registry;
    }

    private static ExecutionRecord NewRecord(string sessionId, string inputText = "real user message") => new()
    {
        ExecutionId = "exec-1",
        SessionId = sessionId,
        Status = ExecutionStatus.Running,
        Input = new ChatInput { Text = inputText },
        Options = new ChatOptions { AgentId = "general" }
    };

    private static InboxBatch NewBatch(string? firstUserMessageId = null) =>
        new([SessionMessage.UserMessage("real user message")], HasUserInput: false, FirstUserText: null,
            FirstUserMessageId: firstUserMessageId);

    private static SkillCatalogSnapshot CreateSnapshot(long version) =>
        new(version, [], new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ConfigLevel>(StringComparer.OrdinalIgnoreCase), [], []);

    // ===== Scheduler 脚手架 =====

    /// <summary>旁路执行用的服务容器（会话管理器 + 技能目录 + 只读装配管道）。</summary>
    private static IServiceProvider CreateBypassServices(
        ISessionManager sessionManager,
        ISkillCatalog catalog,
        RecordingContextAssembler assembler)
    {
        var services = new ServiceCollection()
            .AddSingleton(sessionManager)
            .AddSingleton(catalog)
            .AddSingleton<ISessionToolSchemaResolver>(new StubSessionToolSchemaResolver());

        services.AddStubContextPipeline(assembler);
        return services.BuildServiceProvider();
    }

    private static AgentJob CreateAgentJob(IAgentExecutor executor, IServiceProvider services)
    {
        var agentRegistry = new Mock<IAgentRegistry>();
        agentRegistry
            .Setup(r => r.GetAgentAsync(It.IsAny<string>()))
            .ReturnsAsync(new AgentDefinition { Name = "build", Runtime = AgentRuntime.Native });

        var selectionResolver = new Mock<IAgentSelectionResolver>();
        selectionResolver
            .Setup(r => r.ResolveAgentIdAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("build");

        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(w => w.ProjectSeeingDirectory).Returns(@"E:\root\.seeing");

        var world = new Mock<IExecutionWorld>();
        world.Setup(w => w.Cwd).Returns("root");

        var (hooks, reminders, dispatcher) = CreateSchedulerCollaborators();

        return new AgentJob(
            executor,
            agentRegistry.Object,
            selectionResolver.Object,
            workspace.Object,
            world.Object,
            services,
            hooks,
            reminders,
            dispatcher,
            [],
            NullLogger<AgentJob>.Instance);
    }

    private static HeartbeatJob CreateHeartbeatJob(
        IAgentExecutor executor,
        IServiceProvider services,
        ISessionManager sessionManager)
    {
        var agentRegistry = new Mock<IAgentRegistry>();
        agentRegistry
            .Setup(r => r.GetAgentAsync(It.IsAny<string>()))
            .ReturnsAsync(new AgentDefinition { Name = "build", Runtime = AgentRuntime.Native });

        var selectionResolver = new Mock<IAgentSelectionResolver>();
        selectionResolver
            .Setup(r => r.ResolveAgentIdAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("build");

        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(w => w.ProjectSeeingDirectory).Returns(@"E:\root\.seeing");

        var world = new Mock<IExecutionWorld>();
        world.Setup(w => w.Cwd).Returns("root");

        var (hooks, reminders, dispatcher) = CreateSchedulerCollaborators();

        return new HeartbeatJob(
            executor,
            agentRegistry.Object,
            selectionResolver.Object,
            workspace.Object,
            world.Object,
            sessionManager,
            services,
            hooks,
            reminders,
            dispatcher,
            [],
            NullLogger<HeartbeatJob>.Instance);
    }

    private static (IHookManager Hooks, ISystemReminderRenderer Reminders, IScheduledJobDispatcher Dispatcher)
        CreateSchedulerCollaborators()
    {
        var hooks = new Mock<IHookManager>();
        hooks
            .Setup(h => h.TriggerBlockingAsync(
                It.IsAny<HookSpec>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<IDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HookResult(Continue: true));

        var reminders = new Mock<ISystemReminderRenderer>();
        reminders
            .Setup(r => r.Wrap(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns("wrapped");
        reminders
            .Setup(r => r.TryParse(It.IsAny<string>(), out It.Ref<SystemReminderParts>.IsAny))
            .Returns(false);

        var dispatcher = new Mock<IScheduledJobDispatcher>();
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<DispatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DispatchResult.Ok());

        return (hooks.Object, reminders.Object, dispatcher.Object);
    }

    private static Mock<IJobExecutionContext> CreateJobContextMock()
    {
        var dataMap = new JobDataMap
        {
            { "jobId", "job-1" },
            { "runId", "run-1" },
            { "taskType", ScheduleTaskTypes.Agent },
            { "sessionId", "s-cron" },
            { "agentId", "build" },
            { "prompt", "报时" }
        };

        var jobContext = new Mock<IJobExecutionContext>();
        jobContext.SetupAllProperties();
        jobContext.Setup(c => c.MergedJobDataMap).Returns(dataMap);
        jobContext.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
        jobContext.Setup(c => c.JobDetail).Returns(new JobDetailImpl("job-1", "group", typeof(AgentJob)));
        jobContext.Setup(c => c.FireTimeUtc).Returns(DateTimeOffset.UtcNow);
        jobContext.Setup(c => c.ScheduledFireTimeUtc).Returns((DateTimeOffset?)DateTimeOffset.UtcNow);
        return jobContext;
    }

    private static Mock<IJobExecutionContext> CreateHeartbeatContextMock()
    {
        var dataMap = new JobDataMap
        {
            { "sessionId", "s-hb" },
            { "agentId", "build" },
            { "prompt", "心跳报时" }
        };

        var jobContext = new Mock<IJobExecutionContext>();
        jobContext.SetupAllProperties();
        jobContext.Setup(c => c.MergedJobDataMap).Returns(dataMap);
        jobContext.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
        jobContext.Setup(c => c.JobDetail).Returns(new JobDetailImpl("heartbeat", "group", typeof(HeartbeatJob)));
        jobContext.Setup(c => c.FireTimeUtc).Returns(DateTimeOffset.UtcNow);
        jobContext.Setup(c => c.ScheduledFireTimeUtc).Returns((DateTimeOffset?)DateTimeOffset.UtcNow);
        return jobContext;
    }

    private static async IAsyncEnumerable<IMessageEvent> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<IMessageEvent> OneEvent(IMessageEvent messageEvent)
    {
        yield return messageEvent;
        await Task.CompletedTask;
    }
}

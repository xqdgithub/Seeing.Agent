using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Models;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Compression;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Hosting.Execution;
using Seeing.Agent.Llm;
using Seeing.Agent.Tests.Context;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App.Execution;

/// <summary>
/// 校验执行级上下文作用域携带本轮用户查询：<see cref="Seeing.Agent.Abstractions.Context.IContextExecutionScope.UserQuery"/>
/// 必须取「本批最后一条」用户输入（而非本批第一条），纯系统批次为空。
/// </summary>
public class ExecutionScopeUserQueryTests
{
    [Fact]
    public async Task ExecutionScope_Should_Carry_Last_User_Text_As_UserQuery()
    {
        // Arrange：一批含两条用户消息；本轮输入（ChatInput.Text）是最后一条
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.UserMessage("第一条"));
        session.AddMessage(SessionMessage.UserMessage("最后一条"));

        var assembler = new RecordingContextAssembler();
        var runner = CreateRunner(session, assembler);

        // Act
        await runner.RunOnceAsync(
            NewRecord(session.Id, "最后一条"),
            NewBatch(SessionMessage.UserMessage("第一条"), SessionMessage.UserMessage("最后一条")),
            TestContext.Current.CancellationToken);

        // Assert：装配时作用域里已是最后一条（不是 batch.FirstUserText 的第一条）
        assembler.Scope.Should().NotBeNull();
        assembler.Scope!.UserQuery.Should().Be("最后一条");
    }

    [Fact]
    public async Task ExecutionScope_UserQuery_Should_Be_Empty_For_System_Only_Batch()
    {
        // Arrange：纯系统批次（无用户输入）→ 宿主填 string.Empty 而非 null
        var session = SessionData.Create();

        var assembler = new RecordingContextAssembler();
        var runner = CreateRunner(session, assembler);

        // Act
        await runner.RunOnceAsync(
            NewRecord(session.Id, string.Empty),
            NewBatch(SessionMessage.UserMessage("[system-reminder]纯系统通知[/system-reminder]")),
            TestContext.Current.CancellationToken);

        // Assert
        assembler.Scope.Should().NotBeNull();
        assembler.Scope!.UserQuery.Should().BeNullOrEmpty();
    }

    // ===== 脚手架 =====

    private static ExecutionRecord NewRecord(string sessionId, string inputText) => new()
    {
        ExecutionId = "exec-1",
        SessionId = sessionId,
        Status = ExecutionStatus.Running,
        Input = new ChatInput { Text = inputText },
        Options = new ChatOptions { AgentId = "general", SkipInstructionInject = true }
    };

    private static InboxBatch NewBatch(params SessionMessage[] inputs) =>
        new(inputs, HasUserInput: false, FirstUserText: inputs.Length > 0 ? inputs[0].Content : null);

    private static ExecutionRunner CreateRunner(
        SessionData session,
        RecordingContextAssembler assembler)
    {
        var sessionManager = new Mock<ISessionManager>();
        sessionManager
            .Setup(m => m.EnsureSessionAsync(session.Id, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(session);
        sessionManager.Setup(m => m.Get(It.IsAny<string>())).Returns(session);
        sessionManager.Setup(m => m.SaveAsync(session.Id)).Returns(Task.CompletedTask);
        sessionManager.Setup(m => m.FlushAsync(session.Id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var modelManager = new Mock<IModelManager>();
        modelManager.Setup(m => m.ResolveAcpModel(It.IsAny<string?>(), It.IsAny<string?>())).Returns((string?)null);

        var agentRegistry = new Mock<IAgentRegistry>();
        agentRegistry
            .Setup(r => r.GetAgentAsync(It.IsAny<string>()))
            .ReturnsAsync(new AgentDefinition { Name = "general", Runtime = AgentRuntime.Native });

        var runtimeManager = new Mock<IAgentRuntimeManager>();
        runtimeManager.Setup(r => r.GetDefaultAgentNameAsync()).ReturnsAsync("general");

        var executor = new Mock<IAgentExecutor>();
        executor
            .Setup(e => e.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Returns(EmptyStream());

        var services = new ServiceCollection();
        services.AddSingleton(sessionManager.Object);
        services.AddSingleton(modelManager.Object);
        services.AddSingleton(agentRegistry.Object);
        services.AddSingleton(executor.Object);
        services.AddSingleton(new AgentSelectionResolver(runtimeManager.Object));
        services.AddSingleton(Mock.Of<IWorkspaceProvider>(
            w => w.ProjectSeeingDirectory == Path.Combine("workspace-root", ".seeing")));
        services.AddSingleton(Mock.Of<IExecutionWorld>(w => w.Cwd == "workspace-root"));
        services.AddSingleton(Mock.Of<ICommandRegistry>());
        services.AddSingleton<ISessionToolExposureWriter>(new SessionToolExposureWriter(sessionManager.Object));
        // 执行级上下文管道：装配器持有同 scope 的作用域实例，测试据此观察装配前的事实
        services.AddStubContextPipeline(assembler);
        var provider = services.BuildServiceProvider();

        var configStore = new Mock<IConfigSectionStore>();
        configStore
            .Setup(s => s.GetSection<TokenBudgetAutoCompactionPeek>("TokenBudget"))
            .Returns(new TokenBudgetAutoCompactionPeek { AutoCompactionEnabled = false });

        var publisher = Mock.Of<IExecutionEventPublisher>();

        return new ExecutionRunner(
            provider,
            publisher,
            new ExecutionOptions(),
            configStore.Object,
            new CompactionRunner(
                new CompressionService(null!, sessionManager.Object),
                publisher,
                sessionManager.Object),
            NullLogger<ExecutionRunner>.Instance,
            new StubSessionToolSchemaResolver());
    }

    private static async IAsyncEnumerable<IMessageEvent> EmptyStream()
    {
        yield break;
    }
}

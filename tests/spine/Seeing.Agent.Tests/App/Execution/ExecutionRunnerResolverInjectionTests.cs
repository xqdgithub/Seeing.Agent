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
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Compression;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Core.Instructions;
using Seeing.Agent.Hosting.Execution;
using Seeing.Agent.Llm;
using Seeing.Agent.Tests.Context;
using Seeing.Session.Core;
using System.Collections.Concurrent;
using Xunit;

namespace Seeing.Agent.Tests.App.Execution;

/// <summary>
/// 组6：ExecutionRunner 依赖 ISessionToolSchemaResolver 端口——可替换实现被采用；未注入时构造期失败。
/// </summary>
public class ExecutionRunnerResolverInjectionTests
{
    [Fact]
    public async Task RunOnce_WithCustomResolver_ShouldUseItsSchema()
    {
        // Arrange：自定义 resolver 返回固定 1 条 schema
        var oneSchema = new FunctionToolSchema { Function = new FunctionSchema { Name = "custom_tool", Description = "d" } };
        var customResolver = new Mock<ISessionToolSchemaResolver>();
        customResolver
            .Setup(r => r.ResolveAsync(It.IsAny<SessionData>(), It.IsAny<AgentDefinition>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionToolSchemaResolution(
                new[] { new ExposedTool("custom_tool", "", "custom_tool(): d", oneSchema, 5, ToolExposureMode.Inline) },
                new[] { "tools" },
                ToolExposureLayout.Flat,
                null,
                5,
                0));

        var published = new ConcurrentQueue<(string SessionId, IMessageEvent Event)>();
        var publisher = new Mock<IExecutionEventPublisher>();
        publisher.Setup(p => p.Publish(It.IsAny<string>(), It.IsAny<IMessageEvent>()))
            .Callback((string sessionId, IMessageEvent evt) => published.Enqueue((sessionId, evt)));
        publisher.Setup(p => p.ClearBuffer(It.IsAny<string>()));
        publisher.Setup(p => p.CompleteSession(It.IsAny<string>()));

        AgentContext? capturedContext = null;
        var executor = new Mock<IAgentExecutor>();
        executor.Setup(e => e.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Returns((AgentDefinition _, IReadOnlyList<ChatMessage> _, AgentContext ctx, CancellationToken _) =>
            {
                capturedContext = ctx;
                return EmptyStream();
            });

        var session = SessionData.Create(scenario: "code");
        var runner = CreateRunner(session, publisher.Object, executor.Object, customResolver.Object);

        // Act
        await runner.RunOnceAsync(NewRecord(session.Id), NewBatch(), TestContext.Current.CancellationToken);

        // Assert：执行链采用自定义 resolver 产出的 schema（无需改执行链代码）
        customResolver.Verify(
            r => r.ResolveAsync(It.IsAny<SessionData>(), It.IsAny<AgentDefinition>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        capturedContext.Should().NotBeNull();
        capturedContext!.ToolSettlement!.InlineSchemas.Should().ContainSingle(s => s.Function!.Name == "custom_tool");

        var snapshot = published.Select(p => p.Event).OfType<SchemaSnapshotEvent>().Single();
        snapshot.ToolIds.Should().Contain("custom_tool");
    }

    [Fact]
    public void Ctor_WithoutResolver_ShouldFailFast()
    {
        // Arrange / Act
        var act = () => new ExecutionRunner(
            Mock.Of<IServiceProvider>(),
            Mock.Of<IExecutionEventPublisher>(),
            new ExecutionOptions(),
            Mock.Of<IConfigSectionStore>(),
            new CompactionRunner(
                new CompressionService(null!, Mock.Of<ISessionManager>()),
                Mock.Of<IExecutionEventPublisher>(),
                Mock.Of<ISessionManager>()),
            NullLogger<ExecutionRunner>.Instance,
            null!);

        // Assert：不再自构造回退，缺依赖即构造期明确失败
        act.Should().Throw<ArgumentNullException>().WithParameterName("schemaResolver");
    }

    private static ExecutionRunner CreateRunner(
        SessionData session,
        IExecutionEventPublisher publisher,
        IAgentExecutor executor,
        ISessionToolSchemaResolver resolver)
    {
        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(m => m.EnsureSessionAsync(session.Id, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(session);
        sessionManager.Setup(m => m.Get(It.IsAny<string>())).Returns(session);
        sessionManager.Setup(m => m.SaveAsync(session.Id)).Returns(Task.CompletedTask);
        sessionManager.Setup(m => m.FlushAsync(session.Id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var instructionManager = new Mock<IInstructionManager>();
        instructionManager.Setup(m => m.InjectIfNeededAsync(
                It.IsAny<SessionData>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync(new InstructionInjectResult { Injected = false });

        var modelManager = new Mock<IModelManager>();
        modelManager.Setup(m => m.GetSessionModelRef(It.IsAny<SessionData>())).Returns(string.Empty);
        modelManager.Setup(m => m.ResolveNativeModel(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>()))
            .Returns("m");
        modelManager.Setup(m => m.ResolveAcpModel(It.IsAny<string?>(), It.IsAny<string?>())).Returns((string?)null);

        var agentRegistry = new Mock<IAgentRegistry>();
        agentRegistry.Setup(r => r.GetAgentAsync(It.IsAny<string>()))
            .ReturnsAsync(new AgentDefinition { Name = "general", Runtime = AgentRuntime.Native });

        var runtimeManager = new Mock<IAgentRuntimeManager>();
        runtimeManager.Setup(r => r.GetDefaultAgentNameAsync()).ReturnsAsync("general");

        var services = new ServiceCollection();
        services.AddSingleton(sessionManager.Object);
        services.AddSingleton(instructionManager.Object);
        services.AddSingleton(modelManager.Object);
        services.AddSingleton(agentRegistry.Object);
        services.AddSingleton(executor);
        services.AddSingleton(new AgentSelectionResolver(runtimeManager.Object));
        services.AddSingleton(Mock.Of<IWorkspaceProvider>(w => w.ProjectSeeingDirectory == Path.Combine("workspace-root", ".seeing")));
        services.AddSingleton(Mock.Of<IExecutionWorld>(w => w.Cwd == "workspace-root"));
        services.AddSingleton(Mock.Of<ICommandRegistry>());
        services.AddSingleton<ISessionToolExposureWriter>(new SessionToolExposureWriter(sessionManager.Object));
        // 执行级上下文装配（scoped 装配器 + 执行作用域）：与生产接线同形
        services.AddStubContextPipeline();
        var provider = services.BuildServiceProvider();

        var configStore = new Mock<IConfigSectionStore>();
        configStore.Setup(s => s.GetSection<TokenBudgetAutoCompactionPeek>("TokenBudget"))
            .Returns(new TokenBudgetAutoCompactionPeek { AutoCompactionEnabled = false });

        return new ExecutionRunner(
            provider,
            publisher,
            new ExecutionOptions(),
            configStore.Object,
            new CompactionRunner(
                new CompressionService(null!, Mock.Of<ISessionManager>()),
                Mock.Of<IExecutionEventPublisher>(),
                Mock.Of<ISessionManager>()),
            NullLogger<ExecutionRunner>.Instance,
            resolver);
    }

    private static ExecutionRecord NewRecord(string sessionId) => new()
    {
        ExecutionId = "exec-1",
        SessionId = sessionId,
        Status = ExecutionStatus.Running,
        Input = new ChatInput { Text = "hi" },
        Options = new ChatOptions { AgentId = "general", SkipInstructionInject = true }
    };

    private static InboxBatch NewBatch() =>
        new(new List<SessionMessage>(), HasUserInput: false, FirstUserText: null);

    private static async IAsyncEnumerable<IMessageEvent> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }
}

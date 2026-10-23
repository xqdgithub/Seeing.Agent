using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Models;
using Seeing.Agent.Abstractions.Summarization;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Compression;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Core.Instructions;
using Seeing.Agent.Hosting.Execution;
using Seeing.Agent.Llm;
using Seeing.Session.Core;
using System.Collections.Concurrent;
using Xunit;

namespace Seeing.Agent.Tests.App.Execution;

/// <summary>
/// ExecutionRunner 自动压缩门控测试（原 ExecutionJobService 门控逻辑迁入 RunOnceAsync）。
/// </summary>
public class ExecutionRunnerCompactionTests
{
    [Fact]
    public async Task RunOnce_WhenPendingCompactionAndAutoEnabled_ShouldCompressAndPublishEvents()
    {
        // Arrange
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.UserMessage("问题一"));
        session.AddMessage(SessionMessage.AssistantMessage("回答一"));
        session.PendingCompaction = true;

        var summarizer = new Mock<ISummarizer>();
        summarizer.Setup(s => s.SummarizeAsync(It.IsAny<SummarizeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SummarizeResult(
                "摘要文本",
                new[] { SessionMessage.AssistantMessage("摘要文本"), SessionMessage.AssistantMessage("回答一") },
                100,
                MessagesRemoved: 1));

        var published = new ConcurrentQueue<(string SessionId, IMessageEvent Event)>();
        var publisher = new Mock<IExecutionEventPublisher>();
        publisher.Setup(p => p.Publish(It.IsAny<string>(), It.IsAny<IMessageEvent>()))
            .Callback((string sessionId, IMessageEvent evt) => published.Enqueue((sessionId, evt)));
        publisher.Setup(p => p.ClearBuffer(It.IsAny<string>()));
        publisher.Setup(p => p.CompleteSession(It.IsAny<string>()));

        var configStore = new Mock<IConfigSectionStore>();
        configStore.Setup(s => s.GetSection<TokenBudgetAutoCompactionPeek>("TokenBudget"))
            .Returns(new TokenBudgetAutoCompactionPeek { AutoCompactionEnabled = true });

        var runner = CreateRunner(session, publisher.Object, summarizer.Object, configStore.Object);

        // Act
        await runner.RunOnceAsync(NewRecord(session.Id), NewBatch(), TestContext.Current.CancellationToken);

        // Assert：门控触发压缩
        summarizer.Verify(s => s.SummarizeAsync(
            It.IsAny<SummarizeRequest>(), It.IsAny<CancellationToken>()), Times.Once);

        published.Select(x => x.Event).OfType<CompactionStartedEvent>().Should().NotBeEmpty();
        var completed = published.Select(x => x.Event).OfType<CompactionCompletedEvent>()
            .SingleOrDefault(e => e.MessagesRemoved == 1);
        completed.Should().NotBeNull();
        session.PendingCompaction.Should().BeFalse();
        session.Messages.Should().HaveCount(3);
        session.Messages[0].Content.Should().Be("问题一");
        session.Messages[1].Content.Should().Be("摘要文本");
        session.Messages[1].IsSummary.Should().BeTrue("摘要消息标记 IsSummary");
        session.Messages[2].Content.Should().Be("回答一");
    }

    [Fact]
    public async Task RunOnce_WhenNoPendingCompaction_ShouldNotCompress()
    {
        // Arrange
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.UserMessage("问题一"));
        session.PendingCompaction = false;

        var summarizer = new Mock<ISummarizer>();
        var published = new ConcurrentQueue<(string SessionId, IMessageEvent Event)>();
        var publisher = new Mock<IExecutionEventPublisher>();
        publisher.Setup(p => p.Publish(It.IsAny<string>(), It.IsAny<IMessageEvent>()))
            .Callback((string sessionId, IMessageEvent evt) => published.Enqueue((sessionId, evt)));
        publisher.Setup(p => p.ClearBuffer(It.IsAny<string>()));
        publisher.Setup(p => p.CompleteSession(It.IsAny<string>()));

        var configStore = new Mock<IConfigSectionStore>();
        configStore.Setup(s => s.GetSection<TokenBudgetAutoCompactionPeek>("TokenBudget"))
            .Returns(new TokenBudgetAutoCompactionPeek { AutoCompactionEnabled = true });

        var runner = CreateRunner(session, publisher.Object, summarizer.Object, configStore.Object);

        // Act
        await runner.RunOnceAsync(NewRecord(session.Id), NewBatch(), TestContext.Current.CancellationToken);

        // Assert：门控未触发
        summarizer.Verify(s => s.SummarizeAsync(
            It.IsAny<SummarizeRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        published.Select(x => x.Event).OfType<CompactionStartedEvent>().Should().BeEmpty();
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

    private static ExecutionRunner CreateRunner(
        SessionData session,
        IExecutionEventPublisher publisher,
        ISummarizer summarizer,
        IConfigSectionStore configStore)
    {
        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(m => m.EnsureSessionAsync(session.Id, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(session);
        sessionManager.Setup(m => m.Get(It.IsAny<string>())).Returns(session);
        sessionManager.Setup(m => m.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
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

        var executor = new Mock<IAgentExecutor>();
        executor.Setup(e => e.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Returns((AgentDefinition _, IReadOnlyList<ChatMessage> _, AgentContext _, CancellationToken _) => EmptyStream());

        var services = new ServiceCollection();
        services.AddSingleton(sessionManager.Object);
        services.AddSingleton(instructionManager.Object);
        services.AddSingleton(modelManager.Object);
        services.AddSingleton(agentRegistry.Object);
        services.AddSingleton(executor.Object);
        services.AddSingleton(new AgentSelectionResolver(runtimeManager.Object));
        services.AddSingleton(Mock.Of<IWorkspaceProvider>(w => w.ProjectSeeingDirectory == Path.Combine("workspace-root", ".seeing")));
        services.AddSingleton(Mock.Of<IExecutionWorld>(w => w.Cwd == "workspace-root"));
        services.AddSingleton(Mock.Of<ICommandRegistry>());
        services.AddSingleton<ISessionToolExposureWriter>(new SessionToolExposureWriter(sessionManager.Object));
        var provider = services.BuildServiceProvider();

        return new ExecutionRunner(
            provider,
            publisher,
            new ExecutionOptions(),
            configStore,
            new CompactionRunner(
                new CompressionService(summarizer, sessionManager.Object),
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

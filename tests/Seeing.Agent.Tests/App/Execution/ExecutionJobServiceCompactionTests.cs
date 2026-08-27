using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Summarization;
using Seeing.Agent.Execution;
using Seeing.Agent.Models;
using Seeing.Agent.Compression;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Instructions;
using Seeing.Agent.Llm;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App.Execution;

/// <summary>
/// ExecutionJobService 自动压缩门控测试
/// </summary>
public class ExecutionJobServiceCompactionTests
{
    [Fact]
    public async Task Execute_WhenPendingCompactionAndAutoEnabled_ShouldCompressAndPublishCompletedEvent()
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

        var optionsMonitor = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
        optionsMonitor.Setup(m => m.CurrentValue)
            .Returns(new SeeingAgentOptions
            {
                TokenBudget = new TokenBudgetOptions { AutoCompactionEnabled = true }
            });

        using var fixture = CreateFixture(session, publisher.Object, summarizer.Object, optionsMonitor.Object);
        var service = fixture.Service;

        // Act
        var result = await service.SubmitAsync(
            session.Id,
            ChatInput.FromText("hi"),
            new ChatOptions
            {
                AgentId = "general",
                SkipUserMessagePersist = true,
                SkipInstructionInject = true
            });

        result.Success.Should().BeTrue();

        await WaitUntilAsync(() =>
            published.Any(e => e.Event is ExecutionCompleteEvent));

        // Assert：门控触发压缩
        summarizer.Verify(s => s.SummarizeAsync(
            It.IsAny<SummarizeRequest>(), It.IsAny<CancellationToken>()), Times.Once);

        published.Select(x => x.Event).OfType<CompactionStartedEvent>().Should().NotBeEmpty();
        var completed = published.Select(x => x.Event).OfType<CompactionCompletedEvent>()
            .SingleOrDefault(e => e.MessagesRemoved == 1);
        completed.Should().NotBeNull();
        session.PendingCompaction.Should().BeFalse();
        // 完整历史保留：被压缩部分原样保留 + 摘要消息插入（摘要位置即压缩真相）
        session.Messages.Should().HaveCount(3);
        session.Messages[0].Content.Should().Be("问题一");
        session.Messages[1].Content.Should().Be("摘要文本");
        session.Messages[1].IsSummary.Should().BeTrue("摘要消息标记 IsSummary");
        session.Messages[2].Content.Should().Be("回答一");
        // 传递给 LLM 的统一消息来源只含摘要 + 保留消息
        var active = session.GetActiveMessages();
        active.Should().HaveCount(2);
        active[0].Content.Should().Be("摘要文本");
        active[1].Content.Should().Be("回答一");
    }

    [Fact]
    public async Task Execute_WhenNoPendingCompaction_ShouldNotCompress()
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

        var optionsMonitor = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
        optionsMonitor.Setup(m => m.CurrentValue)
            .Returns(new SeeingAgentOptions
            {
                TokenBudget = new TokenBudgetOptions { AutoCompactionEnabled = true }
            });

        using var fixture = CreateFixture(session, publisher.Object, summarizer.Object, optionsMonitor.Object);
        var service = fixture.Service;

        // Act
        var result = await service.SubmitAsync(
            session.Id,
            ChatInput.FromText("hi"),
            new ChatOptions
            {
                AgentId = "general",
                SkipUserMessagePersist = true,
                SkipInstructionInject = true
            });

        result.Success.Should().BeTrue();

        await WaitUntilAsync(() =>
            published.Any(e => e.Event is ExecutionCompleteEvent));

        // Assert：门控未触发
        summarizer.Verify(s => s.SummarizeAsync(
            It.IsAny<SummarizeRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        published.Select(x => x.Event).OfType<CompactionStartedEvent>().Should().BeEmpty();
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

    private static Fixture CreateFixture(
        SessionData session,
        IExecutionEventPublisher publisher,
        ISummarizer summarizer,
        IOptionsMonitor<SeeingAgentOptions> optionsMonitor)
    {
        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(m => m.EnsureSessionAsync(session.Id, It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(session);
        sessionManager.Setup(m => m.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessionManager.Setup(m => m.SaveAndNotifyAsync(session.Id, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        sessionManager.Setup(m => m.Get(It.IsAny<string>())).Returns((SessionData?)null);
        sessionManager.Setup(m => m.SaveAsync(session.Id)).Returns(Task.CompletedTask);

        var instructionManager = new Mock<IInstructionManager>();
        instructionManager.Setup(m => m.InjectIfNeededAsync(
                It.IsAny<SessionData>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        services.AddSingleton(Mock.Of<IWorkspaceProvider>(w => w.WorkspaceRoot == "workspace-root"));
        services.AddSingleton(Mock.Of<ICommandRegistry>());
        var provider = services.BuildServiceProvider();

        var service = new ExecutionJobService(
            provider,
            publisher,
            new ExecutionOptions(),
            optionsMonitor,
            NullLogger<ExecutionJobService>.Instance,
            new CompactionRunner(
                new CompressionService(summarizer, sessionManager.Object),
                publisher,
                sessionManager.Object));

        return new Fixture(service, provider);
    }

    private static async IAsyncEnumerable<IMessageEvent> EmptyStream()
    {
        yield break;
    }

    private sealed class Fixture(ExecutionJobService service, ServiceProvider provider) : IDisposable
    {
        public ExecutionJobService Service { get; } = service;

        public void Dispose()
        {
            Service.Dispose();
            provider.Dispose();
        }
    }
}
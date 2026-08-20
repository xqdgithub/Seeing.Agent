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
using Seeing.Agent.App.Events;
using Seeing.Agent.App.Execution;
using Seeing.Agent.App.Models;
using Seeing.Agent.Compression;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Instructions;
using Seeing.Agent.Llm;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App.Execution;

/// <summary>
/// 命令短路测试：命令要求结束本轮（shouldContinue=false）时，宿主不得再执行 Agent
/// （避免把命令文本作为普通消息发送给大模型）。
/// </summary>
public class ExecutionJobServiceCommandShortCircuitTests
{
    [Fact]
    public async Task Execute_WhenCommandReturnsShouldContinueFalse_ShouldNotExecuteAgent()
    {
        // Arrange
        var session = SessionData.Create();
        session.Messages.Add(SessionMessage.UserMessage("a"));

        var command = new Mock<ICommand>();
        command.Setup(c => c.Metadata)
            .Returns(CommandMetadata.Simple("compact", "压缩会话", usage: "/compact", category: CommandCategory.Basic));
        command.Setup(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.Ok("Compacted", shouldContinue: false, needsRefresh: true));

        var commandRegistry = new Mock<ICommandRegistry>();
        commandRegistry.Setup(r => r.GetCommand("compact", AgentRuntime.Native))
            .Returns(command.Object);

        var executor = new Mock<IAgentExecutor>();
        executor.Setup(e => e.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Returns(EmptyStream());

        var published = new ConcurrentQueue<(string SessionId, IMessageEvent Event)>();
        var publisher = new Mock<IExecutionEventPublisher>();
        publisher.Setup(p => p.Publish(It.IsAny<string>(), It.IsAny<IMessageEvent>()))
            .Callback((string sessionId, IMessageEvent evt) => published.Enqueue((sessionId, evt)));
        publisher.Setup(p => p.ClearBuffer(It.IsAny<string>()));
        publisher.Setup(p => p.CompleteSession(It.IsAny<string>()));

        var optionsMonitor = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
        optionsMonitor.Setup(m => m.CurrentValue).Returns(new SeeingAgentOptions());

        using var fixture = CreateFixture(
            session, publisher.Object, optionsMonitor.Object, commandRegistry.Object, executor.Object);
        var service = fixture.Service;

        // Act
        var result = await service.SubmitAsync(
            session.Id,
            ChatInput.FromText("/compact"),
            new ChatOptions
            {
                AgentId = "general",
                SkipUserMessagePersist = true,
                SkipInstructionInject = true
            });

        result.Success.Should().BeTrue();

        await WaitUntilAsync(() =>
            published.Any(e => e.Event is ExecutionCompleteEvent));

        // Assert：命令已执行且短路
        command.Verify(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()), Times.Once);
        executor.Verify(e => e.ExecuteAsync(
            It.IsAny<AgentDefinition>(),
            It.IsAny<IReadOnlyList<ChatMessage>>(),
            It.IsAny<AgentContext>(),
            It.IsAny<CancellationToken>()), Times.Never, "shouldContinue=false 的命令不得再执行 Agent");

        var commandEvent = published.Select(x => x.Event).OfType<CommandResultEvent>().Single();
        commandEvent.ShouldContinue.Should().BeFalse("事件应携带短路标志供宿主判定");
    }

    [Fact]
    public async Task Execute_WhenCommandShouldContinue_ShouldExecuteAgent()
    {
        // Arrange
        var session = SessionData.Create();
        session.Messages.Add(SessionMessage.UserMessage("a"));

        var command = new Mock<ICommand>();
        command.Setup(c => c.Metadata)
            .Returns(CommandMetadata.Simple("cont", "继续", category: CommandCategory.Other));
        command.Setup(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.Ok("继续执行", shouldContinue: true));

        var commandRegistry = new Mock<ICommandRegistry>();
        commandRegistry.Setup(r => r.GetCommand("cont", AgentRuntime.Native))
            .Returns(command.Object);

        var executor = new Mock<IAgentExecutor>();
        executor.Setup(e => e.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Returns(EmptyStream());

        var published = new ConcurrentQueue<(string SessionId, IMessageEvent Event)>();
        var publisher = new Mock<IExecutionEventPublisher>();
        publisher.Setup(p => p.Publish(It.IsAny<string>(), It.IsAny<IMessageEvent>()))
            .Callback((string sessionId, IMessageEvent evt) => published.Enqueue((sessionId, evt)));
        publisher.Setup(p => p.ClearBuffer(It.IsAny<string>()));
        publisher.Setup(p => p.CompleteSession(It.IsAny<string>()));

        var optionsMonitor = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
        optionsMonitor.Setup(m => m.CurrentValue).Returns(new SeeingAgentOptions());

        using var fixture = CreateFixture(
            session, publisher.Object, optionsMonitor.Object, commandRegistry.Object, executor.Object);
        var service = fixture.Service;

        // Act
        var result = await service.SubmitAsync(
            session.Id,
            ChatInput.FromText("/cont"),
            new ChatOptions
            {
                AgentId = "general",
                SkipUserMessagePersist = true,
                SkipInstructionInject = true
            });

        result.Success.Should().BeTrue();

        await WaitUntilAsync(() =>
            published.Any(e => e.Event is ExecutionCompleteEvent));

        // Assert：命令执行后继续 Agent
        command.Verify(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()), Times.Once);
        executor.Verify(e => e.ExecuteAsync(
            It.IsAny<AgentDefinition>(),
            It.IsAny<IReadOnlyList<ChatMessage>>(),
            It.IsAny<AgentContext>(),
            It.IsAny<CancellationToken>()), Times.Once, "shouldContinue=true 的命令应继续执行 Agent");
    }

    private static Fixture CreateFixture(
        SessionData session,
        IExecutionEventPublisher publisher,
        IOptionsMonitor<SeeingAgentOptions> optionsMonitor,
        ICommandRegistry commandRegistry,
        IAgentExecutor executor)
    {
        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(m => m.EnsureSessionAsync(session.Id, It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(session);
        sessionManager.Setup(m => m.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        sessionManager.Setup(m => m.SaveAsync(session.Id)).Returns(Task.CompletedTask);
        sessionManager.Setup(m => m.SaveAndNotifyAsync(session.Id, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var modelManager = new Mock<IModelManager>();
        modelManager.Setup(m => m.ResolveAcpModel(It.IsAny<string?>(), It.IsAny<string?>())).Returns((string?)null);

        var agentRegistry = new Mock<IAgentRegistry>();
        agentRegistry.Setup(r => r.GetAgentAsync(It.IsAny<string>()))
            .ReturnsAsync(new AgentDefinition { Name = "general", Runtime = AgentRuntime.Native });

        var runtimeManager = new Mock<IAgentRuntimeManager>();
        runtimeManager.Setup(r => r.GetDefaultAgentNameAsync()).ReturnsAsync("general");

        var services = new ServiceCollection();
        services.AddSingleton(sessionManager.Object);
        services.AddSingleton(Mock.Of<IInstructionManager>());
        services.AddSingleton(modelManager.Object);
        services.AddSingleton(agentRegistry.Object);
        services.AddSingleton(executor);
        services.AddSingleton(new AgentSelectionResolver(runtimeManager.Object));
        services.AddSingleton(Mock.Of<IWorkspaceProvider>(w => w.WorkspaceRoot == "workspace-root"));
        services.AddSingleton(commandRegistry);
        var provider = services.BuildServiceProvider();

        var service = new ExecutionJobService(
            provider,
            publisher,
            new ExecutionOptions(),
            optionsMonitor,
            NullLogger<ExecutionJobService>.Instance,
            new CompactionRunner(
                new CompressionService(null!, sessionManager.Object),
                publisher));

        return new Fixture(service, provider);
    }

    private static async IAsyncEnumerable<IMessageEvent> EmptyStream()
    {
        yield break;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("等待条件超时");
            }
            await Task.Delay(50);
        }
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
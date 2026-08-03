using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.App.Events;
using Seeing.Agent.App.Execution;
using Seeing.Agent.App.Models;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Instructions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App;

public class ExecutionJobServiceInstructionTests
{
    [Fact]
    public async Task SubmitAsync_ShouldInjectBeforeUserMessageAndUpdateWorkingDirectory()
    {
        var session = SessionData.Create();
        session.WorkingDirectory = "old-cwd";
        var instructionManager = CreateInstructionManager(session);
        using var fixture = CreateFixture(session, instructionManager.Object, "workspace-root");

        var result = await fixture.Service.SubmitAsync(
            session.Id,
            new ChatInput { Text = "real user message" },
            new ChatOptions { WorkingDirectory = "resolved-cwd" });

        result.Success.Should().BeTrue();
        session.WorkingDirectory.Should().Be("resolved-cwd");
        session.Messages.Select(message => message.Content)
            .Should().ContainInOrder("injected instructions", "real user message");
        instructionManager.Verify(manager => manager.InjectIfNeededAsync(
            session, "resolved-cwd", "workspace-root", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SubmitAsync_WhenSkippingUserPersist_ShouldStillInjectInstructions()
    {
        var session = SessionData.Create();
        var instructionManager = CreateInstructionManager(session);
        using var fixture = CreateFixture(session, instructionManager.Object, "workspace-root");

        var result = await fixture.Service.SubmitAsync(
            session.Id,
            new ChatInput { Text = "not persisted" },
            new ChatOptions { SkipUserMessagePersist = true });

        result.Success.Should().BeTrue();
        session.Messages.Should().ContainSingle(message => message.Content == "injected instructions");
        instructionManager.Verify(manager => manager.InjectIfNeededAsync(
            session, "workspace-root", "workspace-root", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SubmitAsync_WhenInstructionInjectionIsSkipped_ShouldNotInject()
    {
        var session = SessionData.Create();
        var instructionManager = CreateInstructionManager(session);
        using var fixture = CreateFixture(session, instructionManager.Object, "workspace-root");

        var result = await fixture.Service.SubmitAsync(
            session.Id,
            new ChatInput { Text = "real user message" },
            new ChatOptions { SkipInstructionInject = true });

        result.Success.Should().BeTrue();
        session.Messages.Should().ContainSingle(message => message.Content == "real user message");
        instructionManager.Verify(manager => manager.InjectIfNeededAsync(
            It.IsAny<SessionData>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SubmitAsync_WhenInstructionInjectionFails_ShouldContinueSubmitting()
    {
        var session = SessionData.Create();
        var instructionManager = new Mock<IInstructionManager>();
        instructionManager
            .Setup(manager => manager.InjectIfNeededAsync(
                session, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("injection failed"));
        using var fixture = CreateFixture(session, instructionManager.Object, "workspace-root");

        var result = await fixture.Service.SubmitAsync(
            session.Id,
            new ChatInput { Text = "real user message" },
            options: null);

        result.Success.Should().BeTrue();
        session.Messages.Should().ContainSingle(message => message.Content == "real user message");
    }

    private static Mock<IInstructionManager> CreateInstructionManager(SessionData expectedSession)
    {
        var instructionManager = new Mock<IInstructionManager>();
        instructionManager
            .Setup(manager => manager.InjectIfNeededAsync(
                expectedSession, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<SessionData, string, string, CancellationToken>((session, _, _, _) =>
                session.Messages.Add(SessionMessage.UserMessage("injected instructions")))
            .ReturnsAsync(new InstructionInjectResult { Injected = true });
        return instructionManager;
    }

    private static Fixture CreateFixture(
        SessionData session,
        IInstructionManager instructionManager,
        string workspaceRoot)
    {
        var sessionManager = new Mock<ISessionManager>();
        sessionManager
            .Setup(manager => manager.EnsureSessionAsync(session.Id, It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(session);
        sessionManager.Setup(manager => manager.SaveAsync(session.Id)).Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton(sessionManager.Object);
        services.AddSingleton(CreateModelManager());
        services.AddSingleton(instructionManager);
        services.AddSingleton(Mock.Of<IWorkspaceProvider>(
            workspace => workspace.WorkspaceRoot == workspaceRoot));
        var provider = services.BuildServiceProvider();

        var service = new ExecutionJobService(
            provider,
            Mock.Of<IExecutionEventPublisher>(),
            new ExecutionOptions(),
            Mock.Of<IOptionsMonitor<SeeingAgentOptions>>(
                monitor => monitor.CurrentValue == new SeeingAgentOptions()),
            NullLogger<ExecutionJobService>.Instance);

        return new Fixture(service, provider);
    }

    private static IModelManager CreateModelManager()
    {
        var catalog = new Mock<IModelConfigManager>();
        catalog.Setup(manager => manager.GetModel(It.IsAny<string>())).Returns((string _) => null);
        catalog.Setup(manager => manager.GetModels()).Returns(new Dictionary<string, ModelConfig>());

        var store = new Mock<IAgentStore>();
        store.Setup(manager => manager.GetAsync(It.IsAny<string>())).ReturnsAsync(new AgentDefinition
        {
            Name = "build",
            Runtime = AgentRuntime.Native
        });

        return new ModelManager(catalog.Object, store.Object);
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

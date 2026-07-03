using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;
using Seeing.Agent.Scheduler;
using Seeing.Agent.Scheduler.Execution;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

public class ScheduledAgentRunnerTests
{
    [Fact]
    public async Task RunAsync_UsesExecutionRouter_NotAgentExecutorDirectly()
    {
        var router = new Mock<IAgentExecutionRouter>();
        router.Setup(r => r.ExecuteAsync(It.IsAny<AgentDefinition>(), It.IsAny<AgentContext>(), It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(new StreamCompleteEvent
            {
                SessionId = "main",
                Message = new ChatMessage { Role = ChatRole.Assistant, Content = "done" }
            }));

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetOrCreateAgentInstance("test-agent"))
            .Returns(new TestAgent());

        var resolver = new AgentSelectionResolver(
            Microsoft.Extensions.Options.Options.Create(new SeeingAgentOptions()),
            registry.Object);

        var workspace = new WorkspaceProvider("/tmp");
        var services = new Mock<IServiceProvider>();
        var hooks = new HookManager(NullLogger<HookManager>.Instance);

        var runner = new ScheduledAgentRunner(
            router.Object,
            registry.Object,
            resolver,
            workspace,
            services.Object,
            hooks,
            NullLogger<ScheduledAgentRunner>.Instance);

        var result = await runner.RunAsync(
            ScheduleSources.Cron,
            "hello",
            "test-agent",
            "main",
            30);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("done");
        router.Verify(r => r.ExecuteAsync(
            It.Is<AgentDefinition>(d => d.Name == "test-agent"),
            It.Is<AgentContext>(c => c.Metadata["source"].ToString() == ScheduleSources.Cron),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static async IAsyncEnumerable<IMessageEvent> ToAsyncEnumerable(params IMessageEvent[] events)
    {
        foreach (var evt in events)
            yield return evt;
        await Task.CompletedTask;
    }

    private sealed class TestAgent : AgentBase
    {
        public TestAgent() : base(NullLogger<AgentBase>.Instance) { }
        public override string Name => "test-agent";
        protected override IAsyncEnumerable<ChatMessage> ExecuteCoreAsync(
            ChatMessage input, AgentContext context, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Core.Background;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Scheduling;
using Seeing.Agent.Llm;
using Moq;
using Xunit;

namespace Seeing.Agent.Tests.Scheduling;

public class AgentLoopSchedulerTests
{
    [Fact]
    public async Task TryResumeWhenIdleAsync_WhenBusy_ShouldReturnFalse()
    {
        var scheduler = new AgentLoopScheduler(NullLogger<AgentLoopScheduler>.Instance);
        scheduler.SetLoopBusy("s1", true);
        var called = false;
        scheduler.RegisterResumeHandler((_, _) =>
        {
            called = true;
            return Task.CompletedTask;
        });

        var resumed = await scheduler.TryResumeWhenIdleAsync("s1");

        resumed.Should().BeFalse();
        called.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_ShouldUseProvidedTaskIdAsJobId()
    {
        var registry = new Mock<IAgentRegistry>();
        var mgr = new BackgroundTaskManager(registry.Object, NullLogger<BackgroundTaskManager>.Instance);

        var id = await mgr.StartAsync(new BackgroundTaskLaunchArgs
        {
            TaskId = "child-session-id",
            AgentName = "explore",
            Input = new ChatMessage { Role = ChatRole.User, Content = "hi" },
            Context = new AgentContext { SessionId = "child-session-id", MessageId = "m1" },
            LoopRunner = async ct =>
            {
                await Task.Delay(50, ct);
                return "done";
            }
        });

        id.Should().Be("child-session-id");
        id.Should().NotStartWith("bg_");
        id.Should().NotStartWith("tmp_");
    }

    [Fact]
    public async Task TryResumeWhenIdleAsync_WhenIdle_ShouldInvokeHandler()
    {
        var scheduler = new AgentLoopScheduler(NullLogger<AgentLoopScheduler>.Instance);
        var called = false;
        scheduler.RegisterResumeHandler((_, _) =>
        {
            called = true;
            return Task.CompletedTask;
        });

        var resumed = await scheduler.TryResumeWhenIdleAsync("s1");

        resumed.Should().BeTrue();
        called.Should().BeTrue();
    }
}

using Seeing.Agent.Abstractions.Agents;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Core.Background;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Scheduling;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Session.Core;
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
    public async Task TryResumeWhenIdleAsync_WhenIdleWithoutSynthetic_ShouldSkip()
    {
        var sessions = new Mock<ISessionManager>();
        sessions.Setup(s => s.Get("s1")).Returns(SessionData.Create());
        var scheduler = new AgentLoopScheduler(
            NullLogger<AgentLoopScheduler>.Instance,
            sessions.Object);
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
    public async Task TryResumeWhenIdleAsync_WhenPendingSynthetic_ShouldInvokeHandler()
    {
        var session = SessionData.Create();
        session.Messages.Add(SessionMessage.UserMessage("done").WithMetadata("synthetic", "true"));
        var sessions = new Mock<ISessionManager>();
        sessions.Setup(s => s.Get("s1")).Returns(session);
        var scheduler = new AgentLoopScheduler(
            NullLogger<AgentLoopScheduler>.Instance,
            sessions.Object);
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

    [Fact]
    public void HasPendingSyntheticUserMessage_AfterAssistant_ShouldBeTrue()
    {
        var session = SessionData.Create();
        session.Messages.Add(SessionMessage.UserMessage("hi"));
        session.Messages.Add(SessionMessage.AssistantMessage("hello"));
        session.Messages.Add(SessionMessage.UserMessage("task done").WithMetadata("synthetic", "true"));

        AgentLoopScheduler.HasPendingSyntheticUserMessage(session).Should().BeTrue();
    }

    [Fact]
    public void HasPendingSyntheticUserMessage_OnlyConsumedHistory_ShouldBeFalse()
    {
        var session = SessionData.Create();
        session.Messages.Add(SessionMessage.UserMessage("hi"));
        session.Messages.Add(SessionMessage.AssistantMessage("hello"));

        AgentLoopScheduler.HasPendingSyntheticUserMessage(session).Should().BeFalse();
    }
}

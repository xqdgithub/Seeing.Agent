using Seeing.Agent.Abstractions.Agents;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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
        session.AddMessage(SessionMessage.UserMessage("done").WithMetadata("synthetic", "true"));
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
        session.AddMessage(SessionMessage.UserMessage("hi"));
        session.AddMessage(SessionMessage.AssistantMessage("hello"));
        session.AddMessage(SessionMessage.UserMessage("task done").WithMetadata("synthetic", "true"));

        AgentLoopScheduler.HasPendingSyntheticUserMessage(session).Should().BeTrue();
    }

    [Fact]
    public void HasPendingSyntheticUserMessage_OnlyConsumedHistory_ShouldBeFalse()
    {
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.UserMessage("hi"));
        session.AddMessage(SessionMessage.AssistantMessage("hello"));

        AgentLoopScheduler.HasPendingSyntheticUserMessage(session).Should().BeFalse();
    }
}

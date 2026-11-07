using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Scheduler.Configuration;
using Seeing.Agent.Scheduler.Engine;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

public class SchedulerWorkspaceParticipantTests
{
    [Fact]
    public async Task OnApplying_ShouldStopEngine_AndRememberState()
    {
        var engine = new Mock<IQuartzSchedulerLifecycle>();
        engine.SetupGet(e => e.IsStarted).Returns(true);

        var participant = new SchedulerWorkspaceParticipant(
            engine.Object, NullLogger<SchedulerWorkspaceParticipant>.Instance);

        await participant.OnApplyingAsync(
            new WorkspaceApplyContext(@"C:\a", @"C:\b"), TestContext.Current.CancellationToken);

        engine.Verify(e => e.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
        participant.WasStarted.Should().BeTrue();
    }

    [Fact]
    public async Task OnApplied_ShouldAlwaysRebuild_AndRestartOnlyIfItWasRunning()
    {
        var engine = new Mock<IQuartzSchedulerLifecycle>();
        engine.SetupGet(e => e.IsStarted).Returns(true);

        var participant = new SchedulerWorkspaceParticipant(
            engine.Object, NullLogger<SchedulerWorkspaceParticipant>.Instance);

        await participant.OnApplyingAsync(
            new WorkspaceApplyContext(@"C:\a", @"C:\b"), TestContext.Current.CancellationToken);
        await participant.OnAppliedAsync(
            new WorkspaceApplyContext(@"C:\a", @"C:\b"), TestContext.Current.CancellationToken);

        engine.Verify(e => e.RebuildAsync(It.IsAny<CancellationToken>()), Times.Once,
            "数据库连接串在注册时写死，切换后必须按新根重建");
        engine.Verify(e => e.StartAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnApplied_WhenNotRunning_ShouldRebuildWithoutStarting()
    {
        var engine = new Mock<IQuartzSchedulerLifecycle>();
        engine.SetupGet(e => e.IsStarted).Returns(false);

        var participant = new SchedulerWorkspaceParticipant(
            engine.Object, NullLogger<SchedulerWorkspaceParticipant>.Instance);

        await participant.OnApplyingAsync(
            new WorkspaceApplyContext(@"C:\a", @"C:\b"), TestContext.Current.CancellationToken);
        await participant.OnAppliedAsync(
            new WorkspaceApplyContext(@"C:\a", @"C:\b"), TestContext.Current.CancellationToken);

        engine.Verify(e => e.RebuildAsync(It.IsAny<CancellationToken>()), Times.Once);
        engine.Verify(e => e.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}

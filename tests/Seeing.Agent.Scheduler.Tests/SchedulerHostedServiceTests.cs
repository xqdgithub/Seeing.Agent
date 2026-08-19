using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz.Impl;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Configuration;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Hosting;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

public sealed class SchedulerHostedServiceTests
{
    [Fact]
    public async Task StopAsync_WhenHostDeadlineIsCanceled_ShouldNotThrow()
    {
        var manager = new Mock<IScheduleManager>();
        manager.Setup(x => x.StopAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException());

        var options = new Mock<ISchedulerOptionsProvider>();
        var engine = new QuartzSchedulerEngine(
            new StdSchedulerFactory(),
            NullLogger<QuartzSchedulerEngine>.Instance);
        var service = new ScheduleHostedService(
            manager.Object,
            engine,
            options.Object,
            NullLogger<ScheduleHostedService>.Instance);

        using var stopped = new CancellationTokenSource();
        stopped.Cancel();

        var action = () => service.StopAsync(stopped.Token);

        await action.Should().NotThrowAsync();
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz.Impl;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Configuration;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Hosting;
using Seeing.Agent.Scheduler.Models;
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
            new SchedulerModuleActivity(),
            NullLogger<ScheduleHostedService>.Instance);

        using var stopped = new CancellationTokenSource();
        stopped.Cancel();

        var action = () => service.StopAsync(stopped.Token);

        await action.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_WhenModuleDisabled_ShouldNoOp()
    {
        var manager = new Mock<IScheduleManager>();
        var options = new Mock<ISchedulerOptionsProvider>();
        options.Setup(x => x.Current).Returns(new SchedulerOptions { Enabled = true });

        var catalog = new Mock<IModuleCatalog>();
        catalog.Setup(x => x.IsEnabled("scheduler")).Returns(false);

        var engine = new QuartzSchedulerEngine(
            new StdSchedulerFactory(),
            NullLogger<QuartzSchedulerEngine>.Instance);
        var service = new ScheduleHostedService(
            manager.Object,
            engine,
            options.Object,
            new SchedulerModuleActivity(),
            NullLogger<ScheduleHostedService>.Instance,
            catalog.Object);

        await service.StartAsync(CancellationToken.None);

        manager.Verify(x => x.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_AfterStop_ShouldReviveWhenModuleActive()
    {
        var manager = new Mock<IScheduleManager>();
        manager.Setup(x => x.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        manager.Setup(x => x.StopAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var options = new Mock<ISchedulerOptionsProvider>();
        options.Setup(x => x.Current).Returns(new SchedulerOptions { Enabled = true });

        var catalog = new Mock<IModuleCatalog>();
        catalog.Setup(x => x.IsEnabled("scheduler")).Returns(true);

        var activity = new SchedulerModuleActivity();
        activity.MarkActive();

        var engine = new QuartzSchedulerEngine(
            new StdSchedulerFactory(),
            NullLogger<QuartzSchedulerEngine>.Instance);
        var service = new ScheduleHostedService(
            manager.Object,
            engine,
            options.Object,
            activity,
            NullLogger<ScheduleHostedService>.Instance,
            catalog.Object);

        await service.StartAsync(CancellationToken.None);
        service.IsRunning.Should().BeTrue();
        manager.Verify(x => x.StartAsync(It.IsAny<CancellationToken>()), Times.Once);

        await service.StopAsync(CancellationToken.None);
        service.IsRunning.Should().BeFalse();

        await service.StartAsync(CancellationToken.None);
        service.IsRunning.Should().BeTrue();
        manager.Verify(x => x.StartAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}

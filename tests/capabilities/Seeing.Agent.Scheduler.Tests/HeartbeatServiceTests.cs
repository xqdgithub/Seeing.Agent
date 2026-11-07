using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Management;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Scheduler.Persistence;
using Seeing.Agent.Scheduler.Tests.Fixtures;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

public class HeartbeatServiceTests
{
    [Fact]
    public async Task Get_WhenNotConfigured_ReportsNotConfigured()
    {
        var (service, _, ws) = await BuildAsync();
        using var _ws = ws;

        var snapshot = await service.GetAsync(TestContext.Current.CancellationToken);

        snapshot.Configured.Should().BeFalse();
        snapshot.Enabled.Should().BeFalse();
        snapshot.Status.Should().BeNull();
        snapshot.LastRun.Should().BeNull();
    }

    [Fact]
    public async Task Update_PartialOnly_LeavesOtherFieldsUntouched()
    {
        var (service, _, ws) = await BuildAsync();
        using var _ws = ws;

        await service.UpdateAsync(new HeartbeatUpdate
        {
            Enabled = true,
            Prompt = "检查待办",
            Every = "6h",
            SessionId = "main"
        }, TestContext.Current.CancellationToken);

        var updated = await service.UpdateAsync(
            new HeartbeatUpdate { Enabled = false }, TestContext.Current.CancellationToken);

        updated.Spec!.Prompt.Should().Be("检查待办");
        updated.Spec.Schedule.Every.Should().Be("6h");
        updated.Spec.Dispatch.Target.SessionId.Should().Be("main");
        updated.Spec.Intent.Should().Be(ScheduleIntent.Disabled);
    }

    [Fact]
    public async Task Update_WithInvalidInterval_Throws()
    {
        var (service, _, ws) = await BuildAsync();
        using var _ws = ws;

        var act = () => service.UpdateAsync(new HeartbeatUpdate
        {
            Enabled = true,
            Prompt = "x",
            Every = "6 hours",
            SessionId = "main"
        }, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Update_WithoutTarget_Throws()
    {
        var (service, _, ws) = await BuildAsync();
        using var _ws = ws;

        var act = () => service.UpdateAsync(new HeartbeatUpdate
        {
            Enabled = true,
            Prompt = "x",
            Every = "6h"
        }, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Update_WithoutPrompt_Throws()
    {
        var (service, _, ws) = await BuildAsync();
        using var _ws = ws;

        var act = () => service.UpdateAsync(new HeartbeatUpdate
        {
            Enabled = true,
            Every = "6h",
            SessionId = "main"
        }, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Update_WithOverlappingWindows_Throws()
    {
        var (service, _, ws) = await BuildAsync();
        using var _ws = ws;

        var act = () => service.UpdateAsync(new HeartbeatUpdate
        {
            Enabled = true,
            Prompt = "x",
            Every = "6h",
            SessionId = "main",
            Windows =
            [
                new ScheduleWindow { Start = "08:00", End = "12:00" },
                new ScheduleWindow { Start = "11:00", End = "14:00" }
            ]
        }, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RunNow_NotConfigured_IsNotFound()
    {
        var (service, _, ws) = await BuildAsync();
        using var _ws = ws;

        var result = await service.RunNowAsync(TestContext.Current.CancellationToken);

        result.Should().BeOfType<TriggerResult.NotFound>();
    }

    [Fact]
    public async Task RunNow_SchedulerNotStarted_IsNotAccepted()
    {
        var (service, _, ws) = await BuildAsync();
        using var _ws = ws;

        await service.UpdateAsync(new HeartbeatUpdate
        {
            Enabled = true,
            Prompt = "x",
            Every = "6h",
            SessionId = "main"
        }, TestContext.Current.CancellationToken);

        var result = await service.RunNowAsync(TestContext.Current.CancellationToken);

        result.Should().NotBeOfType<TriggerResult.Accepted>(
            "调度器没跑时不能报「已触发」");
    }

    [Fact]
    public async Task GetHistory_NotConfigured_IsEmpty()
    {
        var (service, _, ws) = await BuildAsync();
        using var _ws = ws;

        var history = await service.GetHistoryAsync(20, 0, TestContext.Current.CancellationToken);

        history.Should().BeEmpty();
    }

    private static async Task<(IHeartbeatService Service, ScheduleManager Manager, SchedulerTestWorkspace Ws)>
        BuildAsync()
    {
        var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson();
        var (manager, _) = SchedulerIntegrationTestsFactory.CreatePair(ws);
        var repo = new JsonScheduleRepository(ws.Workspace, NullLogger<JsonScheduleRepository>.Instance);
        var service = new HeartbeatService(manager, repo, NullLogger<HeartbeatService>.Instance);
        await Task.CompletedTask;
        return (service, manager, ws);
    }
}

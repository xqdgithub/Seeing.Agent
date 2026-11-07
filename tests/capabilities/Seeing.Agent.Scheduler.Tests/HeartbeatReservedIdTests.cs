using FluentAssertions;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Scheduler.Tests.Fixtures;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

/// <summary>保留 id 只属于平台内部任务，不得经用户任务 API 进入 Jobs。</summary>
public class HeartbeatReservedIdTests
{
    [Fact]
    public void IsReserved_IsTrueOnlyForHeartbeatId()
    {
        ScheduleIdPolicy.IsReserved(SchedulerConstants.HeartbeatJobId).Should().BeTrue();
        ScheduleIdPolicy.IsReserved("job_lunch").Should().BeFalse();
        ScheduleIdPolicy.IsReserved("_heartbeat2").Should().BeFalse();
        ScheduleIdPolicy.IsReserved(null).Should().BeFalse();
    }

    [Fact]
    public async Task RunJobOnce_ForReservedId_ReturnsNotFound()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson();
        var (manager, _) = SchedulerIntegrationTestsFactory.CreatePair(ws);

        await manager.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var result = await manager.RunJobOnceAsync(
                SchedulerConstants.HeartbeatJobId, TestContext.Current.CancellationToken);

            result.Should().BeOfType<TriggerResult.NotFound>(
                "心跳的立即执行只走 IHeartbeatService，cron_run 不得触发它");
        }
        finally
        {
            await manager.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task CreateOrReplace_ForReservedId_Throws()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson();
        var (manager, _) = SchedulerIntegrationTestsFactory.CreatePair(ws);

        var spec = new ScheduledJobSpec
        {
            Id = SchedulerConstants.HeartbeatJobId,
            Intent = ScheduleIntent.Active,
            Prompt = "x",
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "1h" }
        };

        var act = () => manager.CreateOrReplaceJobAsync(spec, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Delete_ForReservedId_Throws()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson();
        var (manager, _) = SchedulerIntegrationTestsFactory.CreatePair(ws);

        var act = () => manager.DeleteJobAsync(
            SchedulerConstants.HeartbeatJobId, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SetIntent_ForReservedId_Throws()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson();
        var (manager, _) = SchedulerIntegrationTestsFactory.CreatePair(ws);

        var act = () => manager.SetJobIntentAsync(
            SchedulerConstants.HeartbeatJobId, ScheduleIntent.Paused, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}

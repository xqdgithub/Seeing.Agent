using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Agent.Scheduler.Jobs;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Scheduler.Persistence;
using Seeing.Agent.Scheduler.Tests.Fixtures;
using Seeing.Serialization;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

/// <summary>心跳是 jobs.json 里的独立槽位：不在 Jobs 内，null 表示未配置。</summary>
public class HeartbeatSlotTests
{
    [Fact]
    public void JobsFile_WithoutHeartbeatSlot_DeserializesToNull()
    {
        var json = """{"version":2,"jobs":[]}""";
        var file = System.Text.Json.JsonSerializer.Deserialize<JobsFile>(json);

        file.Should().NotBeNull();
        file!.Heartbeat.Should().BeNull("未知槽位必须是「未配置」，不能凭空生成默认任务");
        file.Jobs.Should().BeEmpty();
    }

    [Fact]
    public void JobsFile_RoundTripsHeartbeatSlot()
    {
        var file = new JobsFile
        {
            Heartbeat = new ScheduledJobSpec
            {
                Id = SchedulerConstants.HeartbeatJobId,
                Intent = ScheduleIntent.Active,
                TaskType = ScheduleTaskTypes.Agent,
                Prompt = "报时",
                Schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "6h" },
                Dispatch = new DispatchSpec { Target = new DispatchTarget { SessionId = "main" } }
            }
        };

        var json = System.Text.Json.JsonSerializer.Serialize(file, SeeingJson.ReadableIndented);
        var back = System.Text.Json.JsonSerializer.Deserialize<JobsFile>(json);

        back!.Heartbeat.Should().NotBeNull();
        back.Heartbeat!.Prompt.Should().Be("报时");
        back.Jobs.Should().BeEmpty("心跳不得混进用户任务集合");
    }

    [Fact]
    public async Task SaveHeartbeat_RegistersQuartzJobWithHeartbeatProfile()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson();
        var (manager, engine) = SchedulerIntegrationTestsFactory.CreatePair(ws);
        await manager.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            await manager.SaveHeartbeatAsync(NewSpec(ScheduleIntent.Active),
                TestContext.Current.CancellationToken);

            var status = await manager.GetJobStatusAsync(
                SchedulerConstants.HeartbeatJobId, TestContext.Current.CancellationToken);
            status.State.Should().Be(JobState.Scheduled);

            var detail = await engine.Scheduler!.GetJobDetail(
                new Quartz.JobKey(SchedulerConstants.HeartbeatJobId, SchedulerConstants.DefaultJobGroup));
            detail.Should().NotBeNull();
            detail!.JobType.Should().Be(typeof(AgentJob), "心跳与定时任务共用同一个 Job 类型");

            var profileJson = detail.JobDataMap.GetString(JobDataKeys.RunProfile);
            profileJson.Should().NotBeNullOrWhiteSpace();
            var profile = System.Text.Json.JsonSerializer.Deserialize<ScheduleRunProfile>(profileJson!);
            profile!.Source.Should().Be(ScheduleSources.Heartbeat);
            profile.Layout.Should().Be(DispatchLayout.AssistantOnly);
        }
        finally
        {
            await manager.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task SaveHeartbeat_Disabled_RemovesQuartzJobButKeepsSlot()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson();
        var (manager, _) = SchedulerIntegrationTestsFactory.CreatePair(ws);
        await manager.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            await manager.SaveHeartbeatAsync(NewSpec(ScheduleIntent.Disabled),
                TestContext.Current.CancellationToken);

            var status = await manager.GetJobStatusAsync(
                SchedulerConstants.HeartbeatJobId, TestContext.Current.CancellationToken);
            status.State.Should().Be(JobState.Disabled);

            var repo = new JsonScheduleRepository(ws.Workspace, NullLogger<JsonScheduleRepository>.Instance);
            var file = await repo.LoadAsync(TestContext.Current.CancellationToken);
            file.Heartbeat.Should().NotBeNull("禁用只移除触发器，槽位必须保留以便用户重新启用");
            file.Jobs.Should().BeEmpty();
        }
        finally
        {
            await manager.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task HeartbeatSlot_IsInvisibleToJobListAndStatuses()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson();
        var (manager, _) = SchedulerIntegrationTestsFactory.CreatePair(ws);
        await manager.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            await manager.SaveHeartbeatAsync(NewSpec(ScheduleIntent.Active),
                TestContext.Current.CancellationToken);

            (await manager.ListJobsAsync(TestContext.Current.CancellationToken))
                .Should().BeEmpty("心跳不属于定时任务面");
            (await manager.GetAllJobStatusesAsync(TestContext.Current.CancellationToken))
                .Should().BeEmpty("心跳状态只经 IHeartbeatService 暴露");
        }
        finally
        {
            await manager.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static ScheduledJobSpec NewSpec(ScheduleIntent intent) => new()
    {
        Id = SchedulerConstants.HeartbeatJobId,
        Name = "心跳",
        Intent = intent,
        TaskType = ScheduleTaskTypes.Agent,
        Prompt = "报时",
        Schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "6h" },
        Dispatch = new DispatchSpec { Target = new DispatchTarget { SessionId = "main" } }
    };
}

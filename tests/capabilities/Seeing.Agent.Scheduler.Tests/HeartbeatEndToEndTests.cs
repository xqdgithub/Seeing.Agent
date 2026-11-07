using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Agent.Scheduler;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Jobs;
using Seeing.Agent.Scheduler.Management;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Scheduler.Persistence;
using Seeing.Agent.Scheduler.Tests.Fixtures;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

/// <summary>
/// 心跳端到端闭环：保存 → 注册 → 立即执行 → 历史落盘 → 结果投递。
/// <para>
/// 这条用例覆盖设计里最容易被"看起来配好了"掩盖的四件事：执行真的发生了、
/// 历史真的写了、投递真的发生了、cron 面真的看不见它。
/// </para>
/// </summary>
public class HeartbeatEndToEndTests
{
    [Fact]
    public async Task SaveRun_RunsAgentDispatchesOnceAndRecordsHistory()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson();

        var dispatched = new List<DispatchRequest>();
        var dispatchedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var dispatcher = new DispatchRecorder(dispatched, dispatchedSignal);
        var (manager, _) = SchedulerIntegrationTestsFactory.CreatePair(ws, dispatcher: dispatcher);

        var repo = new JsonScheduleRepository(ws.Workspace, NullLogger<JsonScheduleRepository>.Instance);
        var service = new HeartbeatService(manager, repo, NullLogger<HeartbeatService>.Instance);

        await manager.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var updated = await service.UpdateAsync(new HeartbeatUpdate
            {
                Enabled = true,
                Prompt = "检查待办",
                Every = "6h",
                SessionId = "hb-target",
                Agent = "test-agent"
            }, TestContext.Current.CancellationToken);

            updated.Configured.Should().BeTrue();
            updated.Spec!.Schedule.Every.Should().Be("6h");

            var trigger = await service.RunNowAsync(TestContext.Current.CancellationToken);
            trigger.Should().BeOfType<TriggerResult.Accepted>();

            await dispatchedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // 身份：来源与布局都必须是心跳，而不是 cron
            dispatched.Should().ContainSingle();
            dispatched[0].Source.Should().Be(ScheduleSources.Heartbeat);
            dispatched[0].Layout.Should().Be(DispatchLayout.AssistantOnly);
            dispatched[0].ReminderKind.Should().Be("heartbeat");
            dispatched[0].SenderId.Should().Be("heartbeat");
            dispatched[0].SessionId.Should().Be("hb-target");

            // 历史：投递发生在通知监听器之前，需等落盘完成（条件轮询，命中即返回）
            var history = await WaitForHistoryAsync(service);
            history.Should().NotBeEmpty("心跳执行历史必须落盘");
            history[0].Source.Should().Be(ScheduleSources.Heartbeat);
            history[0].Status.Should().Be("success");

            // 槽位：LastRunAt 已回写（interval 锚点依赖它）
            var slot = await repo.LoadAsync(TestContext.Current.CancellationToken);
            slot.Heartbeat.Should().NotBeNull();
            slot.Heartbeat!.LastRunAt.Should().NotBeNull();
            slot.Jobs.Should().BeEmpty("心跳不得进入用户任务集合");

            // cron 面：列表与状态都看不到它
            (await manager.ListJobsAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
            (await manager.GetAllJobStatusesAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
        }
        finally
        {
            await manager.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task DisablingHeartbeat_RemovesTriggerButKeepsSlotAndStopsRunNow()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson();

        var (manager, _) = SchedulerIntegrationTestsFactory.CreatePair(ws);
        var repo = new JsonScheduleRepository(ws.Workspace, NullLogger<JsonScheduleRepository>.Instance);
        var service = new HeartbeatService(manager, repo, NullLogger<HeartbeatService>.Instance);

        await manager.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            await service.UpdateAsync(new HeartbeatUpdate
            {
                Enabled = true, Prompt = "x", Every = "1h", SessionId = "main"
            }, TestContext.Current.CancellationToken);

            await service.UpdateAsync(
                new HeartbeatUpdate { Enabled = false }, TestContext.Current.CancellationToken);

            var snapshot = await service.GetAsync(TestContext.Current.CancellationToken);
            snapshot.Configured.Should().BeTrue("禁用不等于删除配置");
            snapshot.Enabled.Should().BeFalse();
            snapshot.Status!.State.Should().Be(JobState.Disabled);
            snapshot.Status.NextFireTime.Should().BeNull();

            (await service.RunNowAsync(TestContext.Current.CancellationToken))
                .Should().BeOfType<TriggerResult.Disabled>();
        }
        finally
        {
            await manager.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Heartbeat_DoesNotLeakIntoJobsFile()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson();

        var (manager, _) = SchedulerIntegrationTestsFactory.CreatePair(ws);
        var repo = new JsonScheduleRepository(ws.Workspace, NullLogger<JsonScheduleRepository>.Instance);
        var service = new HeartbeatService(manager, repo, NullLogger<HeartbeatService>.Instance);
        await manager.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            await service.UpdateAsync(new HeartbeatUpdate
            {
                Enabled = true, Prompt = "x", Every = "1h", SessionId = "main"
            }, TestContext.Current.CancellationToken);

            // 用户任务照常可建，且与心跳互不干扰
            await manager.CreateOrReplaceJobAsync(new ScheduledJobSpec
            {
                Id = "job_lunch",
                Intent = ScheduleIntent.Active,
                TaskType = ScheduleTaskTypes.Text,
                Text = "吃饭",
                Schedule = new ScheduleSpec { Type = ScheduleTypes.Cron, Cron = "0 12 * * *" },
                Dispatch = new DispatchSpec { Target = new DispatchTarget { SessionId = "main" } }
            }, TestContext.Current.CancellationToken);

            var jobs = await manager.ListJobsAsync(TestContext.Current.CancellationToken);
            jobs.Should().ContainSingle().Which.Id.Should().Be("job_lunch");

            var statuses = await manager.GetAllJobStatusesAsync(TestContext.Current.CancellationToken);
            statuses.Should().ContainSingle().Which.JobId.Should().Be("job_lunch");

            var file = await repo.LoadAsync(TestContext.Current.CancellationToken);
            file.Jobs.Should().ContainSingle();
            file.Heartbeat.Should().NotBeNull();
        }
        finally
        {
            await manager.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>条件轮询等历史落盘：命中即返回，超时才失败（不靠固定等待）。</summary>
    private static async Task<IReadOnlyList<JobExecutionRecord>> WaitForHistoryAsync(
        IHeartbeatService service,
        int timeoutSeconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        IReadOnlyList<JobExecutionRecord> latest = Array.Empty<JobExecutionRecord>();

        while (DateTime.UtcNow < deadline)
        {
            latest = await service.GetHistoryAsync(20, 0, TestContext.Current.CancellationToken);
            if (latest.Count > 0)
                return latest;

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        return latest;
    }

    private sealed class DispatchRecorder(
        List<DispatchRequest> captured,
        TaskCompletionSource signal) : IScheduledJobDispatcher
    {
        public Task<DispatchResult> DispatchAsync(DispatchRequest request, CancellationToken ct = default)
        {
            captured.Add(request);
            signal.TrySetResult();
            return Task.FromResult(DispatchResult.Ok());
        }
    }
}

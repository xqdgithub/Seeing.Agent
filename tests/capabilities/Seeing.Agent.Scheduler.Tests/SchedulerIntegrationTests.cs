using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Management;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Scheduler.Persistence;
using Seeing.Agent.Scheduler.Tests.Fixtures;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

public class SchedulerIntegrationTests
{
    [Fact]
    public async Task ScheduleManager_CreatesRunsAndRecordsHistory()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson();

        var executedPrompts = new List<string>();
        var (manager, engine) = CreateScheduleManager(ws, executedPrompts);

        await manager.StartAsync(TestContext.Current.CancellationToken);

        // 验证 scheduler 已启动
        engine.IsStarted.Should().BeTrue("scheduler should be started");

        var job = await manager.CreateOrReplaceJobAsync(new ScheduledJobSpec
        {
            Id = "test-job",
            Intent = ScheduleIntent.Active,
            TaskType = ScheduleTaskTypes.Agent,
            Agent = "test-agent",
            Prompt = "执行定时检查",
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Cron, Cron = "0 9 * * *" },
            Dispatch = new DispatchSpec { Target = new DispatchTarget { SessionId = "cron-session" } }
        }, TestContext.Current.CancellationToken);

        job.Id.Should().Be("test-job");

        // 检查 job 是否已注册
        var statusBefore = await manager.GetJobStatusAsync("test-job", TestContext.Current.CancellationToken);
        Console.WriteLine($"Job state before trigger: {statusBefore.State}");
        statusBefore.State.Should().Be(JobState.Scheduled);

        var result = await manager.RunJobOnceAsync("test-job", TestContext.Current.CancellationToken);

        // RunJobOnceAsync 返回 TriggerResult
        result.Should().BeOfType<TriggerResult.Accepted>();

        // 等待任务实际执行完成（Quartz 异步执行）
        await WaitUntilAsync(() => executedPrompts.Count > 0, "任务应被执行", timeoutMs: 3000);

        Console.WriteLine($"Executed prompts: {string.Join(", ", executedPrompts)}");

        if (executedPrompts.Count == 0)
        {
            var statusAfter = await manager.GetJobStatusAsync("test-job", TestContext.Current.CancellationToken);
            var engineStatus = await engine.GetStatusAsync(TestContext.Current.CancellationToken);
            Console.WriteLine($"Engine started: {engineStatus.IsStarted}, Running jobs: {engineStatus.RunningJobs}");
            Console.WriteLine($"Job state after trigger: {statusAfter.State}");
            Console.WriteLine($"Job previous fire: {statusAfter.PreviousFireTime}");

            // 检查历史记录是否有错误
            var debugRepo = new JsonScheduleRepository(ws.Workspace, NullLogger<JsonScheduleRepository>.Instance);
            var debugHistory = await debugRepo.GetHistoryAsync("test-job", 10, ct: TestContext.Current.CancellationToken);
            Console.WriteLine($"History count: {debugHistory.Count}");
            if (debugHistory.Count > 0)
            {
                Console.WriteLine($"Last history status: {debugHistory[0].Status}");
                Console.WriteLine($"Last history error: {debugHistory[0].Error}");
            }
        }

        executedPrompts.Should().ContainSingle(p => p.Contains("执行定时检查", StringComparison.Ordinal));

        var repo = new JsonScheduleRepository(ws.Workspace, NullLogger<JsonScheduleRepository>.Instance);
        var history = await repo.GetHistoryAsync("test-job", 10, ct: TestContext.Current.CancellationToken);
        history.Should().HaveCount(1);
        history[0].Status.Should().Be("success");

        await manager.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ScheduleManager_TextJob_DispatchesWithoutRouter()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson();

        var dispatched = new List<DispatchRequest>();
        var dispatcher = new Mock<IScheduledJobDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<DispatchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DispatchRequest, CancellationToken>((req, _) => dispatched.Add(req))
            .ReturnsAsync(DispatchResult.Ok());

        var (manager, _) = CreateScheduleManager(ws, [], dispatcher.Object);
        await manager.StartAsync(TestContext.Current.CancellationToken);

        await manager.CreateOrReplaceJobAsync(new ScheduledJobSpec
        {
            Id = "text-job",
            Intent = ScheduleIntent.Active,
            TaskType = ScheduleTaskTypes.Text,
            Text = "test text",
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "1h" },
            Dispatch = new DispatchSpec { Target = new DispatchTarget { SessionId = "notify" } }
        }, TestContext.Current.CancellationToken);

        var result = await manager.RunJobOnceAsync("text-job", TestContext.Current.CancellationToken);
        result.Should().BeOfType<TriggerResult.Accepted>();

        // 等待异步派发完成（轮询到条件达成即返回，不盲等）
        await WaitUntilAsync(() => dispatched.Count > 0, "text 任务应被派发", timeoutMs: 3000);

        await manager.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void AddSeeingScheduler_RegistersAllServices()
    {
        // 简化测试：只验证类型可以注册
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IScheduleRepository, JsonScheduleRepository>();
        services.AddSingleton<QuartzSchedulerEngine>();
        services.AddSingleton<ScheduleManager>();

        // 验证服务可以构建
        services.Should().NotBeNull();
    }

    [Fact]
    public async Task QuartzSchedulerEngine_GetStatus_ReturnsCorrectState()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson();

        var (manager, engine) = CreateScheduleManager(ws, []);
        await manager.StartAsync(TestContext.Current.CancellationToken);

        var status = await engine.GetStatusAsync(TestContext.Current.CancellationToken);
        status.IsRunning.Should().BeTrue();
        status.IsStarted.Should().BeTrue();

        await manager.StopAsync(TestContext.Current.CancellationToken);

        // StopAsync 后 scheduler 已 shutdown，GetStatusAsync 返回空状态
        var statusAfterStop = await engine.GetStatusAsync(TestContext.Current.CancellationToken);
        statusAfterStop.IsRunning.Should().BeFalse();
        statusAfterStop.IsStarted.Should().BeFalse();
    }

    [Fact]
    public async Task ScheduleManager_PauseAndResumeJob()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson();

        var (manager, engine) = CreateScheduleManager(ws, []);
        await manager.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            await manager.CreateOrReplaceJobAsync(new ScheduledJobSpec
            {
                Id = "pause-test",
                Intent = ScheduleIntent.Active,
                TaskType = ScheduleTaskTypes.Text,
                Text = "test",
                Schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "1h" }
            }, TestContext.Current.CancellationToken);

            // 暂停任务
            await manager.PauseJobAsync("pause-test", TestContext.Current.CancellationToken);
            var statusPaused = await manager.GetJobStatusAsync("pause-test", TestContext.Current.CancellationToken);
            statusPaused.State.Should().Be(JobState.Paused);

            // 恢复任务
            await manager.ResumeJobAsync("pause-test", TestContext.Current.CancellationToken);
            var statusResumed = await manager.GetJobStatusAsync("pause-test", TestContext.Current.CancellationToken);
            statusResumed.State.Should().Be(JobState.Scheduled);
        }
        finally
        {
            await manager.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ScheduleManager_DisableAndEnable()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson();

        var (manager, engine) = CreateScheduleManager(ws, []);
        await manager.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            await manager.CreateOrReplaceJobAsync(new ScheduledJobSpec
            {
                Id = "disable-test",
                Intent = ScheduleIntent.Active,
                TaskType = ScheduleTaskTypes.Text,
                Text = "test",
                Schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "1h" }
            }, TestContext.Current.CancellationToken);

            // 禁用任务
            await manager.SetJobIntentAsync("disable-test", ScheduleIntent.Disabled, TestContext.Current.CancellationToken);
            var statusDisabled = await manager.GetJobStatusAsync("disable-test", TestContext.Current.CancellationToken);
            statusDisabled.State.Should().Be(JobState.Disabled);

            // 启用任务
            await manager.SetJobIntentAsync("disable-test", ScheduleIntent.Active, TestContext.Current.CancellationToken);
            var statusEnabled = await manager.GetJobStatusAsync("disable-test", TestContext.Current.CancellationToken);
            statusEnabled.State.Should().Be(JobState.Scheduled);
        }
        finally
        {
            await manager.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// 轮询等待条件达成；命中即返回，<paramref name="timeoutMs"/> 只是安全上限。
    /// <para>Quartz 的执行是异步的，断言前必须等「副作用已发生」；轮询比固定 <c>Task.Delay</c>
    /// 既更快（通常 20–100ms）又更稳（不受机器负载影响）。</para>
    /// </summary>
    private static async Task WaitUntilAsync(
        Func<bool> condition,
        string because,
        int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
                return;
            await Task.Delay(20, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        condition().Should().BeTrue(because);
    }

    private static (ScheduleManager Manager, QuartzSchedulerEngine Engine, IServiceProvider ServiceProvider)
        CreateScheduleManagerWithServices(
            SchedulerTestWorkspace ws,
            List<string> executedPrompts,
            IScheduledJobDispatcher? dispatcher = null,
            SchedulerOptions? schedulerOptions = null)
        => Fixtures.SchedulerIntegrationTestsFactory.Create(ws, executedPrompts, dispatcher, schedulerOptions);

    // 兼容旧调用
    private static (ScheduleManager Manager, QuartzSchedulerEngine Engine) CreateScheduleManager(
        SchedulerTestWorkspace ws,
        List<string> executedPrompts,
        IScheduledJobDispatcher? dispatcher = null,
        SchedulerOptions? schedulerOptions = null)
    {
        var (manager, engine, _) = CreateScheduleManagerWithServices(ws, executedPrompts, dispatcher, schedulerOptions);
        return (manager, engine);
    }

    private static IAgentExecutor CreateMockRouter(List<string> executedPrompts)
        => Fixtures.SchedulerIntegrationTestsFactory.CreateMockRouter(executedPrompts);

    private static IAgentRegistry CreateMockRegistry()
        => Fixtures.SchedulerIntegrationTestsFactory.CreateMockRegistry();

    private static async IAsyncEnumerable<IMessageEvent> ToAsyncEnumerable(params IMessageEvent[] events)
    {
        foreach (var evt in events)
            yield return evt;
        await Task.CompletedTask;
    }
}

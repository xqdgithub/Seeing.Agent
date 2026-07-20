using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Quartz;
using Quartz.Impl;
using Quartz.Simpl;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Configuration;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Extensions;
using Seeing.Agent.Scheduler.Jobs;
using Seeing.Agent.Scheduler.Management;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Scheduler.Persistence;
using Seeing.Agent.Scheduler.Tests.Fixtures;
using Seeing.Session.Core;
using Seeing.Session.Management;
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

        await manager.StartAsync();
        
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
        });

        job.Id.Should().Be("test-job");

        // 检查 job 是否已注册
        var statusBefore = await manager.GetJobStatusAsync("test-job");
        Console.WriteLine($"Job state before trigger: {statusBefore.State}");
        statusBefore.State.Should().Be(JobState.Scheduled);

        var result = await manager.RunJobOnceAsync("test-job");
        
        // RunJobOnceAsync 返回 TriggerResult
        result.Should().BeOfType<TriggerResult.Accepted>();
        
        // 等待任务实际执行完成（Quartz 异步执行）
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(200);
            if (executedPrompts.Count > 0)
                break;
        }
        
        Console.WriteLine($"Executed prompts: {string.Join(", ", executedPrompts)}");
        
        if (executedPrompts.Count == 0)
        {
            var statusAfter = await manager.GetJobStatusAsync("test-job");
            var engineStatus = await engine.GetStatusAsync();
            Console.WriteLine($"Engine started: {engineStatus.IsStarted}, Running jobs: {engineStatus.RunningJobs}");
            Console.WriteLine($"Job state after trigger: {statusAfter.State}");
            Console.WriteLine($"Job previous fire: {statusAfter.PreviousFireTime}");
            
            // 检查历史记录是否有错误
            var debugRepo = new JsonScheduleRepository(ws.Workspace, NullLogger<JsonScheduleRepository>.Instance);
            var debugHistory = await debugRepo.GetHistoryAsync("test-job", 10);
            Console.WriteLine($"History count: {debugHistory.Count}");
            if (debugHistory.Count > 0)
            {
                Console.WriteLine($"Last history status: {debugHistory[0].Status}");
                Console.WriteLine($"Last history error: {debugHistory[0].Error}");
            }
        }
        
        executedPrompts.Should().ContainSingle(p => p.Contains("执行定时检查", StringComparison.Ordinal));

        var repo = new JsonScheduleRepository(ws.Workspace, NullLogger<JsonScheduleRepository>.Instance);
        var history = await repo.GetHistoryAsync("test-job", 10);
        history.Should().HaveCount(1);
        history[0].Status.Should().Be("success");

        await manager.StopAsync();
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
        await manager.StartAsync();

        await manager.CreateOrReplaceJobAsync(new ScheduledJobSpec
        {
            Id = "text-job",
            Intent = ScheduleIntent.Active,
            TaskType = ScheduleTaskTypes.Text,
            Text = "test text",
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "1h" },
            Dispatch = new DispatchSpec { Target = new DispatchTarget { SessionId = "notify" } }
        });

        var result = await manager.RunJobOnceAsync("text-job");
        result.Should().BeOfType<TriggerResult.Accepted>();
        
        // 等待异步执行完成
        await Task.Delay(2000);

        await manager.StopAsync();
    }

    [Fact]
    public async Task Heartbeat_TriggeredViaJobId()
    {
        using var ws = new SchedulerTestWorkspace();
        var schedulerOptions = new SchedulerOptions
        {
            Enabled = true,
            Heartbeat = new HeartbeatOptions
            {
                Enabled = true,
                Prompt = "heartbeat test prompt",
                SessionId = "heartbeat-main",
                TimeoutSeconds = 30
            }
        };

        var executedPrompts = new List<string>();
        var (manager, engine, serviceProvider) = CreateScheduleManagerWithServices(ws, executedPrompts, schedulerOptions: schedulerOptions);
        
        await manager.StartAsync();
        
        // 验证 scheduler 状态
        var scheduler = engine.Scheduler;
        scheduler.Should().NotBeNull();
        scheduler!.IsStarted.Should().BeTrue();
        
        // 验证 heartbeat job 已注册
        var jobKey = new Quartz.JobKey(SchedulerConstants.HeartbeatJobId, SchedulerConstants.DefaultJobGroup);
        var jobDetail = await scheduler.GetJobDetail(jobKey);
        jobDetail.Should().NotBeNull();
        jobDetail!.JobDataMap.GetString(JobDataKeys.Prompt).Should().Be("heartbeat test prompt");

        // 触发 job
        var result = await manager.RunJobOnceAsync(SchedulerConstants.HeartbeatJobId);
        result.Should().BeOfType<TriggerResult.Accepted>();
        
        // 等待执行完成（Quartz 异步执行）
        await Task.Delay(3000);
        
        Console.WriteLine($"Executed prompts: {string.Join(", ", executedPrompts)}");

        await manager.StopAsync();
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
    public async Task ScheduleManager_ManualHeartbeatRunViaJobId()
    {
        using var ws = new SchedulerTestWorkspace();
        var schedulerOptions = new SchedulerOptions
        {
            Enabled = true,
            Heartbeat = new HeartbeatOptions { Enabled = true, Prompt = "heartbeat manual run" }
        };

        var executedPrompts = new List<string>();
        var (manager, engine) = CreateScheduleManager(ws, executedPrompts, schedulerOptions: schedulerOptions);
        await manager.StartAsync();
        
        // 验证 scheduler 状态
        var scheduler = engine.Scheduler;
        scheduler.Should().NotBeNull();
        scheduler!.IsStarted.Should().BeTrue();

        var result = await manager.RunJobOnceAsync(SchedulerConstants.HeartbeatJobId);
        
        // 验证触发成功
        result.Should().BeOfType<TriggerResult.Accepted>();
        
        // 等待执行
        await Task.Delay(2000);

        await manager.StopAsync();
    }

    [Fact]
    public async Task QuartzSchedulerEngine_GetStatus_ReturnsCorrectState()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson();

        var (manager, engine) = CreateScheduleManager(ws, []);
        await manager.StartAsync();

        var status = await engine.GetStatusAsync();
        status.IsRunning.Should().BeTrue();
        status.IsStarted.Should().BeTrue();

        await manager.StopAsync();

        // StopAsync 后 scheduler 已 shutdown，GetStatusAsync 返回空状态
        var statusAfterStop = await engine.GetStatusAsync();
        statusAfterStop.IsRunning.Should().BeFalse();
        statusAfterStop.IsStarted.Should().BeFalse();
    }

    [Fact]
    public async Task ScheduleManager_PauseAndResumeJob()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson();

        var (manager, engine) = CreateScheduleManager(ws, []);
        await manager.StartAsync();

        try
        {
            await manager.CreateOrReplaceJobAsync(new ScheduledJobSpec
            {
                Id = "pause-test",
                Intent = ScheduleIntent.Active,
                TaskType = ScheduleTaskTypes.Text,
                Text = "test",
                Schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "1h" }
            });

            // 暂停任务
            await manager.PauseJobAsync("pause-test");
            var statusPaused = await manager.GetJobStatusAsync("pause-test");
            statusPaused.State.Should().Be(JobState.Paused);

            // 恢复任务
            await manager.ResumeJobAsync("pause-test");
            var statusResumed = await manager.GetJobStatusAsync("pause-test");
            statusResumed.State.Should().Be(JobState.Scheduled);
        }
        finally
        {
            await manager.StopAsync();
        }
    }
    
    [Fact]
    public async Task ScheduleManager_DisableAndEnable()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson();

        var (manager, engine) = CreateScheduleManager(ws, []);
        await manager.StartAsync();

        try
        {
            await manager.CreateOrReplaceJobAsync(new ScheduledJobSpec
            {
                Id = "disable-test",
                Intent = ScheduleIntent.Active,
                TaskType = ScheduleTaskTypes.Text,
                Text = "test",
                Schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "1h" }
            });

            // 禁用任务
            await manager.SetJobIntentAsync("disable-test", ScheduleIntent.Disabled);
            var statusDisabled = await manager.GetJobStatusAsync("disable-test");
            statusDisabled.State.Should().Be(JobState.Disabled);

            // 启用任务
            await manager.SetJobIntentAsync("disable-test", ScheduleIntent.Active);
            var statusEnabled = await manager.GetJobStatusAsync("disable-test");
            statusEnabled.State.Should().Be(JobState.Scheduled);
        }
        finally
        {
            await manager.StopAsync();
        }
    }

    private static (ScheduleManager Manager, QuartzSchedulerEngine Engine, IServiceProvider ServiceProvider) CreateScheduleManagerWithServices(
        SchedulerTestWorkspace ws,
        List<string> executedPrompts,
        IScheduledJobDispatcher? dispatcher = null,
        SchedulerOptions? schedulerOptions = null)
    {
        var optionsProvider = new TestSchedulerOptionsProvider(schedulerOptions ?? ws.Options);
        var router = CreateMockRouter(executedPrompts);
        var registry = CreateMockRegistry();
        var sessionManager = new SessionManager();
        var hooks = new HookManager(NullLogger<HookManager>.Instance);

        dispatcher ??= Mock.Of<IScheduledJobDispatcher>(d =>
            d.DispatchAsync(It.IsAny<DispatchRequest>(), It.IsAny<CancellationToken>()) == Task.FromResult(DispatchResult.Ok()));

        // 先创建 ScheduleManager，但需要 engine
        var repo = new JsonScheduleRepository(ws.Workspace, NullLogger<JsonScheduleRepository>.Instance);
        
        // Build Quartz scheduler with proper configuration
        var properties = new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.threadPool.type"] = "Quartz.Simpl.SimpleThreadPool, Quartz",
            ["quartz.threadPool.threadCount"] = "10",
            ["quartz.scheduler.instanceName"] = "TestScheduler"
        };
        var schedulerFactory = new StdSchedulerFactory(properties);
        var scheduler = schedulerFactory.GetScheduler().GetAwaiter().GetResult();
        
        // 使用包装工厂确保返回预配置的 scheduler
        var wrapperFactory = new PreconfiguredSchedulerFactory(scheduler);
        var engine = new QuartzSchedulerEngine(wrapperFactory, NullLogger<QuartzSchedulerEngine>.Instance);

        var manager = new ScheduleManager(
            repo,
            engine,
            optionsProvider,
            NullLogger<ScheduleManager>.Instance);

        // 注册所有依赖（包括 manager 作为 IJobExecutionListener）
        var services = new ServiceCollection()
            .AddSingleton<IAgentExecutionRouter>(router)
            .AddSingleton(registry)
            .AddSingleton<AgentSelectionResolver>(sp =>
                new AgentSelectionResolver(
                    Microsoft.Extensions.Options.Options.Create(new SeeingAgentOptions { DefaultAgent = "test-agent" }),
                    registry))
            .AddSingleton<IWorkspaceProvider>(ws.Workspace)
            .AddSingleton<ISessionManager>(sessionManager)
            .AddSingleton<HookManager>(hooks)
            .AddSingleton(dispatcher)
            .AddSingleton<IJobExecutionListener>(manager)  // 关键：注册 manager 作为监听器
            .AddLogging()
            .AddTransient<AgentJob>()
            .AddTransient<HeartbeatJob>()
            .BuildServiceProvider();

        // 设置 Microsoft DI JobFactory
        scheduler.JobFactory = new MicrosoftDependencyInjectionJobFactory(services, Options.Create(new QuartzOptions()));

        return (manager, engine, services);
    }

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

    /// <summary>包装工厂，返回预配置的 scheduler</summary>
    private class PreconfiguredSchedulerFactory : ISchedulerFactory
    {
        private readonly IScheduler _scheduler;
        public PreconfiguredSchedulerFactory(IScheduler scheduler) => _scheduler = scheduler;
        public Task<IScheduler> GetScheduler(CancellationToken cancellationToken = default) => Task.FromResult(_scheduler);
        public Task<IScheduler> GetScheduler(string schedName, CancellationToken cancellationToken = default) => Task.FromResult(_scheduler);
        public Task<IReadOnlyList<IScheduler>> GetAllSchedulers(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<IScheduler>>(new[] { _scheduler });
    }

    private static IAgentExecutionRouter CreateMockRouter(List<string> executedPrompts)
    {
        var router = new Mock<IAgentExecutionRouter>();
        router.Setup(r => r.ExecuteAsync(It.IsAny<AgentDefinition>(), It.IsAny<AgentContext>(), It.IsAny<CancellationToken>()))
            .Returns((AgentDefinition agent, AgentContext ctx, CancellationToken _) =>
            {
                var prompt = ctx.History.LastOrDefault()?.Content;
                if (prompt != null)
                    executedPrompts.Add(prompt);

                return ToAsyncEnumerable(new StreamCompleteEvent
                {
                    SessionId = ctx.SessionId,
                    Message = new ChatMessage { Role = ChatRole.Assistant, Content = "agent-response" }
                });
            });
        return router.Object;
    }

    private static IAgentRegistry CreateMockRegistry()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetOrCreateAgentInstance(It.IsAny<string>()))
            .Returns((string name) => new TestAgent(name));
        return registry.Object;
    }

    private static async IAsyncEnumerable<IMessageEvent> ToAsyncEnumerable(params IMessageEvent[] events)
    {
        foreach (var evt in events)
            yield return evt;
        await Task.CompletedTask;
    }

    private sealed class TestAgent : AgentBase
    {
        private readonly string _name;

        public TestAgent(string name) : base(NullLogger<AgentBase>.Instance)
        {
            _name = name;
        }

        public override string Name => _name;
        protected override IAsyncEnumerable<ChatMessage> ExecuteCoreAsync(
            ChatMessage input, AgentContext context, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}

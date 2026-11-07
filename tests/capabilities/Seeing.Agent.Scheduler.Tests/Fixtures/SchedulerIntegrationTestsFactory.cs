using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Quartz;
using Quartz.Impl;
using Quartz.Simpl;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Reminders;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Jobs;
using Seeing.Agent.Scheduler.Management;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Scheduler.Persistence;
using Seeing.Session.Core;
using Seeing.Session.Management;

namespace Seeing.Agent.Scheduler.Tests.Fixtures;

/// <summary>
/// 搭建真实 Quartz + <see cref="ScheduleManager"/> 的测试工厂。
/// <para>
/// 多个用例类都要跑「注册任务 → 触发 → 断言副作用」，重复搭 Quartz 既慢又容易漂移，
/// 因此把装配收敛到这里。心跳与定时任务共用同一个 Job 类型，工厂只注册 <c>AgentJob</c>。
/// </para>
/// </summary>
internal static class SchedulerIntegrationTestsFactory
{
    public static (ScheduleManager Manager, QuartzSchedulerEngine Engine, IServiceProvider Services) Create(
        SchedulerTestWorkspace ws,
        List<string>? executedPrompts = null,
        IScheduledJobDispatcher? dispatcher = null,
        SchedulerOptions? schedulerOptions = null,
        ISessionToolSchemaResolver? toolSchemaResolver = null)
    {
        executedPrompts ??= new List<string>();

        var optionsProvider = new TestSchedulerOptionsProvider(schedulerOptions ?? ws.Options);
        var router = CreateMockRouter(executedPrompts);
        var registry = CreateMockRegistry();
        var sessionManager = new SessionManager();
        var hooks = new HookManager(NullLogger<HookManager>.Instance);

        dispatcher ??= Mock.Of<IScheduledJobDispatcher>(d =>
            d.DispatchAsync(It.IsAny<DispatchRequest>(), It.IsAny<CancellationToken>())
            == Task.FromResult(DispatchResult.Ok()));

        var repo = new JsonScheduleRepository(ws.Workspace, NullLogger<JsonScheduleRepository>.Instance);

        var properties = new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.threadPool.type"] = "Quartz.Simpl.SimpleThreadPool, Quartz",
            ["quartz.threadPool.threadCount"] = "10",
            ["quartz.scheduler.instanceName"] = "TestScheduler_" + Guid.NewGuid().ToString("N")[..8]
        };
        var schedulerFactory = new StdSchedulerFactory(properties);
        var scheduler = schedulerFactory.GetScheduler().GetAwaiter().GetResult();

        var wrapperFactory = new PreconfiguredSchedulerFactory(scheduler);
        var engine = new QuartzSchedulerEngine(wrapperFactory, NullLogger<QuartzSchedulerEngine>.Instance);

        var manager = new ScheduleManager(
            repo,
            engine,
            optionsProvider,
            NullLogger<ScheduleManager>.Instance);

        var serviceCollection = new ServiceCollection()
            .AddSingleton(router)
            .AddSingleton(registry)
            .AddSingleton<IAgentSelectionResolver>(_ =>
                new AgentSelectionResolver(new Mock<IAgentRuntimeManager>().Object))
            .AddSingleton<IWorkspaceProvider>(ws.Workspace)
            .AddSingleton(Mock.Of<IExecutionWorld>(w => w.Cwd == ws.Workspace.GetProjectRoot()))
            .AddSingleton<ISessionManager>(sessionManager)
            .AddSingleton<IHookManager>(hooks)
            .AddSingleton<ISystemReminderRenderer, Seeing.Agent.Core.Reminders.SystemReminderRendererService>()
            .AddSingleton(dispatcher)
            .AddSingleton<IJobExecutionListener>(manager)
            .AddLogging()
            .AddTransient<AgentJob>();

        if (toolSchemaResolver is not null)
        {
            // 需要真实工具视图的用例才注入；其余走「无 resolver → 空 schema」降级路径。
            serviceCollection.AddSingleton(toolSchemaResolver);
        }

        var services = serviceCollection.BuildServiceProvider();

        scheduler.JobFactory = new MicrosoftDependencyInjectionJobFactory(
            services, Options.Create(new QuartzOptions()));

        return (manager, engine, services);
    }

    /// <summary>只要管理器与引擎时的便捷重载。</summary>
    public static (ScheduleManager Manager, QuartzSchedulerEngine Engine) CreatePair(
        SchedulerTestWorkspace ws,
        List<string>? executedPrompts = null,
        IScheduledJobDispatcher? dispatcher = null,
        SchedulerOptions? schedulerOptions = null)
    {
        var (manager, engine, _) = Create(ws, executedPrompts, dispatcher, schedulerOptions);
        return (manager, engine);
    }

    public static IAgentExecutor CreateMockRouter(List<string> executedPrompts)
    {
        var router = new Mock<IAgentExecutor>();
        router.Setup(r => r.ExecuteAsync(
                It.IsAny<AgentDefinition>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<AgentContext>(),
                It.IsAny<CancellationToken>()))
            .Returns((AgentDefinition _, IReadOnlyList<ChatMessage> messages, AgentContext ctx, CancellationToken _) =>
            {
                var prompt = messages.LastOrDefault()?.Content;
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

    public static IAgentRegistry CreateMockRegistry()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetAgentAsync(It.IsAny<string>()))
            .ReturnsAsync((string name) => new AgentDefinition { Name = name });
        return registry.Object;
    }

    public static async IAsyncEnumerable<IMessageEvent> ToAsyncEnumerable(params IMessageEvent[] events)
    {
        foreach (var evt in events)
            yield return evt;
        await Task.CompletedTask;
    }

    /// <summary>包装工厂，返回预配置的 scheduler。</summary>
    private sealed class PreconfiguredSchedulerFactory : ISchedulerFactory
    {
        private readonly IScheduler _scheduler;

        public PreconfiguredSchedulerFactory(IScheduler scheduler) => _scheduler = scheduler;

        public Task<IScheduler> GetScheduler(CancellationToken cancellationToken = default)
            => Task.FromResult(_scheduler);

        public Task<IScheduler?> GetScheduler(string schedName, CancellationToken cancellationToken = default)
            => Task.FromResult<IScheduler?>(_scheduler);

        public Task<IReadOnlyList<IScheduler>> GetAllSchedulers(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<IScheduler>>(new[] { _scheduler });
    }
}

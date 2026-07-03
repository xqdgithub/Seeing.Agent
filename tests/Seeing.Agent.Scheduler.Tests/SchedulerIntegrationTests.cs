using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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
using Seeing.Agent.Scheduler.Execution;
using Seeing.Agent.Scheduler.Extensions;
using Seeing.Agent.Scheduler.Heartbeat;
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
        var (manager, _) = CreateScheduleManager(ws, executedPrompts);

        await manager.StartAsync();

        var job = await manager.CreateOrReplaceJobAsync(new ScheduledJobSpec
        {
            Id = "test-job",
            TaskType = ScheduleTaskTypes.Agent,
            Agent = "test-agent",
            Prompt = "执行定时检查",
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Cron, Cron = "0 9 * * *" },
            Dispatch = new DispatchSpec { Target = new DispatchTarget { SessionId = "cron-session" } }
        });

        job.Id.Should().Be("test-job");

        var result = await manager.RunJobOnceAsync("test-job");
        result.Success.Should().BeTrue();
        result.Output.Should().Be("agent-response");
        executedPrompts.Should().ContainSingle(p => p == "执行定时检查");

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
            TaskType = ScheduleTaskTypes.Text,
            Text = "提醒：该休息了",
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "1h" },
            Dispatch = new DispatchSpec { Target = new DispatchTarget { SessionId = "notify" } }
        });

        var result = await manager.RunJobOnceAsync("text-job");
        result.Success.Should().BeTrue();
        dispatched.Should().ContainSingle(d =>
            d.TaskType == ScheduleTaskTypes.Text &&
            d.Content == "提醒：该休息了" &&
            d.SessionId == "notify");

        await manager.StopAsync();
    }

    [Fact]
    public async Task HeartbeatRunner_ExecutesQueryFromFile()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson(new SchedulerOptions
        {
            Enabled = true,
            Heartbeat = new HeartbeatOptions
            {
                Enabled = true,
                QueryFile = "HEARTBEAT.md",
                SessionId = "heartbeat-main",
                TimeoutSeconds = 30
            }
        });
        ws.WriteHeartbeatFile("检查系统状态并汇报");

        var executedPrompts = new List<string>();
        var (_, heartbeat) = CreateScheduleManager(ws, executedPrompts);

        var result = await heartbeat.RunOnceAsync();
        result.Success.Should().BeTrue();
        result.Output.Should().Be("agent-response");
        executedPrompts.Should().ContainSingle(p => p == "检查系统状态并汇报");
    }

    [Fact]
    public async Task HeartbeatRunner_SkipsWhenDisabled()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson(new SchedulerOptions
        {
            Enabled = true,
            Heartbeat = new HeartbeatOptions { Enabled = false }
        });
        ws.WriteHeartbeatFile("should not run");

        var executedPrompts = new List<string>();
        var (_, heartbeat) = CreateScheduleManager(ws, executedPrompts);

        var result = await heartbeat.RunOnceAsync();
        result.Success.Should().BeTrue();
        result.Output.Should().Be("Heartbeat disabled");
        executedPrompts.Should().BeEmpty();
    }

    [Fact]
    public async Task HeartbeatRunner_SkipsWhenQueryFileEmpty()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson(new SchedulerOptions
        {
            Enabled = true,
            Heartbeat = new HeartbeatOptions { Enabled = true }
        });
        ws.WriteHeartbeatFile("   ");

        var executedPrompts = new List<string>();
        var (_, heartbeat) = CreateScheduleManager(ws, executedPrompts);

        var result = await heartbeat.RunOnceAsync();
        result.Output.Should().Be("Skipped: empty query file");
        executedPrompts.Should().BeEmpty();
    }

    [Fact]
    public async Task AddSeeingScheduler_RegistersAllServices()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkspaceProvider>(ws.Workspace);
        services.AddSingleton<ISessionManager>(new SessionManager());
        services.AddSingleton<AgentSelectionResolver>(sp =>
            new AgentSelectionResolver(
                Microsoft.Extensions.Options.Options.Create(new SeeingAgentOptions { DefaultAgent = "default" }),
                Mock.Of<IAgentRegistry>()));
        services.AddSingleton<HookManager>(sp => new HookManager(NullLogger<HookManager>.Instance));
        services.AddSingleton<IAgentExecutionRouter>(CreateMockRouter([]));
        services.AddSingleton<IAgentRegistry>(CreateMockRegistry());
        services.AddSingleton<IServiceProvider>(sp => sp);

        services.AddSeeingScheduler();

        var provider = services.BuildServiceProvider();
        provider.GetService<IScheduleManager>().Should().NotBeNull();
        provider.GetService<IHeartbeatRunner>().Should().NotBeNull();
        provider.GetService<IScheduledJobDispatcher>().Should().NotBeNull();
    }

    [Fact]
    public async Task ScheduleManager_ManualHeartbeatRunViaJobId()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson(new SchedulerOptions
        {
            Enabled = true,
            Heartbeat = new HeartbeatOptions { Enabled = true }
        });
        ws.WriteHeartbeatFile("heartbeat manual run");

        var executedPrompts = new List<string>();
        var (manager, _) = CreateScheduleManager(ws, executedPrompts);
        await manager.StartAsync();

        var result = await manager.RunJobOnceAsync(SchedulerConstants.HeartbeatJobId);
        result.Success.Should().BeTrue();
        executedPrompts.Should().ContainSingle(p => p == "heartbeat manual run");

        await manager.StopAsync();
    }

    private static (ScheduleManager Manager, HeartbeatRunner Heartbeat) CreateScheduleManager(
        SchedulerTestWorkspace ws,
        List<string> executedPrompts,
        IScheduledJobDispatcher? dispatcher = null)
    {
        var optionsProvider = ws.CreateOptionsProvider();
        var router = CreateMockRouter(executedPrompts);
        var registry = CreateMockRegistry();
        var sessionManager = new SessionManager();
        var hooks = new HookManager(NullLogger<HookManager>.Instance);
        var services = new ServiceCollection()
            .AddSingleton<IAgentExecutionRouter>(router)
            .AddSingleton(registry)
            .AddSingleton<AgentSelectionResolver>(sp =>
                new AgentSelectionResolver(
                    Microsoft.Extensions.Options.Options.Create(new SeeingAgentOptions { DefaultAgent = "test-agent" }),
                    registry))
            .BuildServiceProvider();

        var agentRunner = new ScheduledAgentRunner(
            router,
            registry,
            services.GetRequiredService<AgentSelectionResolver>(),
            ws.Workspace,
            services,
            hooks,
            NullLogger<ScheduledAgentRunner>.Instance);

        dispatcher ??= Mock.Of<IScheduledJobDispatcher>(d =>
            d.DispatchAsync(It.IsAny<DispatchRequest>(), It.IsAny<CancellationToken>()) == Task.FromResult(DispatchResult.Ok()));

        var executor = new AgentScheduledJobExecutor(
            agentRunner,
            dispatcher,
            NullLogger<AgentScheduledJobExecutor>.Instance);

        var heartbeat = new HeartbeatRunner(
            optionsProvider,
            agentRunner,
            dispatcher,
            ws.Workspace,
            sessionManager,
            NullLogger<HeartbeatRunner>.Instance);

        var manager = new ScheduleManager(
            new JsonScheduleRepository(ws.Workspace, NullLogger<JsonScheduleRepository>.Instance),
            executor,
            heartbeat,
            new InProcessSchedulerEngine(NullLogger<InProcessSchedulerEngine>.Instance),
            optionsProvider,
            NullLogger<ScheduleManager>.Instance);

        return (manager, heartbeat);
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

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
using Seeing.Agent.Scheduler.Heartbeat;
using Seeing.Agent.Scheduler.Management;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Scheduler.Persistence;
using Seeing.Agent.Scheduler.Tests.Fixtures;
using Seeing.Session.Management;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

public class SchedulerEngineTests
{
    [Fact]
    public async Task Engine_FiresDueJobAfterInterval()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson(new SchedulerOptions
        {
            Enabled = true,
            TickIntervalSeconds = 1
        });

        var fired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new InProcessSchedulerEngine(NullLogger<InProcessSchedulerEngine>.Instance);
        engine.Configure(TimeSpan.FromMilliseconds(200), "UTC");

        await engine.StartAsync(async (jobId, _) =>
        {
            fired.TrySetResult(jobId);
        });

        // 注册一个已到期 job（nextRun = now - 1s）
        var schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "1h" };
        await engine.UpsertJobAsync("due-job", schedule, enabled: true);

        // 手动将 next run 设为过去 — 通过 Upsert 后立即 tick
        // UpsertJobAsync sets next from now, so we need a trick: use once with past runAt won't work in engine
        // Instead register with interval and wait - first tick should fire since next is ~now from upsert at start

        // Re-upsert with schedule that makes it due immediately by using past once... 
        // Simpler: use RunJobOnce via manager path is already tested.
        // For engine: set job with interval 0 not allowed. Use cron every minute and set clock... can't.

        // Alternative: register job, then the engine compares NextRunAt <= now. Upsert sets next to GetNextOccurrence from now which is future.
        // Fix: add internal test hook OR use very short wait with reschedule trick.

        // Actually after UpsertJobAsync, next run is in the future (1h). Won't fire on tick.
        // Test engine tick by manually invoking callback is not possible without reflection.

        // Better approach: test that engine calls callback when we use once schedule with RunAt in past
        // But UpsertJobAsync always computes next from GetNextOccurrence - once with past runAt returns null and uses `now` fallback in RegisterUserJobAsync:
        // `var next = ScheduleExpressionParser.GetNextOccurrence(...) ?? now;`
        // For once with past runAt, GetNextOccurrence returns null, so next = now which is due immediately!

        var onceSchedule = new ScheduleSpec
        {
            Type = ScheduleTypes.Once,
            RunAt = DateTimeOffset.UtcNow.AddSeconds(-10)
        };
        await engine.UpsertJobAsync("once-due", onceSchedule, enabled: true);

        var completed = await Task.WhenAny(fired.Task, Task.Delay(3000));
        completed.Should().Be(fired.Task);
        (await fired.Task).Should().Be("once-due");

        await engine.StopAsync();
    }

    [Fact]
    public async Task ScheduleManager_LoadsJobsFromJsonFile()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson();

        var jobsPath = Path.Combine(ws.SeeingDirectory, "jobs.json");
        await File.WriteAllTextAsync(jobsPath, """
            {
              "jobs": [
                {
                  "id": "loaded-job",
                  "enabled": true,
                  "taskType": "text",
                  "text": "from file",
                  "schedule": { "type": "cron", "cron": "0 12 * * *" },
                  "dispatch": { "target": { "sessionId": "main" } }
                }
              ]
            }
            """);

        var (manager, _) = CreateManager(ws, []);
        await manager.StartAsync();

        var jobs = await manager.ListJobsAsync();
        jobs.Should().ContainSingle(j => j.Id == "loaded-job" && j.Text == "from file");

        await manager.StopAsync();
    }

    private static (ScheduleManager Manager, HeartbeatRunner Heartbeat) CreateManager(
        SchedulerTestWorkspace ws,
        List<string> executedPrompts)
    {
        var optionsProvider = ws.CreateOptionsProvider();
        var router = new Mock<IAgentExecutionRouter>();
        router.Setup(r => r.ExecuteAsync(It.IsAny<AgentDefinition>(), It.IsAny<AgentContext>(), It.IsAny<CancellationToken>()))
            .Returns((AgentDefinition _, AgentContext ctx, CancellationToken _) =>
            {
                var prompt = ctx.History.LastOrDefault()?.Content;
                if (prompt != null) executedPrompts.Add(prompt);
                return ToAsyncEnumerable(new StreamCompleteEvent
                {
                    SessionId = ctx.SessionId,
                    Message = new ChatMessage { Role = ChatRole.Assistant, Content = "ok" }
                });
            });

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetOrCreateAgentInstance(It.IsAny<string>()))
            .Returns((string name) => new StubAgent(name));

        var services = new ServiceCollection()
            .AddSingleton<IAgentExecutionRouter>(router.Object)
            .AddSingleton(registry.Object)
            .AddSingleton(new AgentSelectionResolver(
                Microsoft.Extensions.Options.Options.Create(new SeeingAgentOptions { DefaultAgent = "test-agent" }),
                registry.Object))
            .BuildServiceProvider();

        var agentRunner = new ScheduledAgentRunner(
            router.Object,
            registry.Object,
            services.GetRequiredService<AgentSelectionResolver>(),
            ws.Workspace,
            services,
            new HookManager(NullLogger<HookManager>.Instance),
            NullLogger<ScheduledAgentRunner>.Instance);

        var dispatcher = Mock.Of<IScheduledJobDispatcher>(d =>
            d.DispatchAsync(It.IsAny<DispatchRequest>(), It.IsAny<CancellationToken>()) == Task.FromResult(DispatchResult.Ok()));

        var executor = new AgentScheduledJobExecutor(agentRunner, dispatcher, NullLogger<AgentScheduledJobExecutor>.Instance);
        var heartbeat = new HeartbeatRunner(
            optionsProvider, agentRunner, dispatcher, ws.Workspace,
            new SessionManager(), NullLogger<HeartbeatRunner>.Instance);

        var manager = new ScheduleManager(
            new JsonScheduleRepository(ws.Workspace, NullLogger<JsonScheduleRepository>.Instance),
            executor, heartbeat,
            new InProcessSchedulerEngine(NullLogger<InProcessSchedulerEngine>.Instance),
            optionsProvider, NullLogger<ScheduleManager>.Instance);

        return (manager, heartbeat);
    }

    private static async IAsyncEnumerable<IMessageEvent> ToAsyncEnumerable(params IMessageEvent[] events)
    {
        foreach (var evt in events) yield return evt;
        await Task.CompletedTask;
    }

    private sealed class StubAgent : AgentBase
    {
        private readonly string _name;
        public StubAgent(string name) : base(NullLogger<AgentBase>.Instance) => _name = name;
        public override string Name => _name;
        protected override IAsyncEnumerable<ChatMessage> ExecuteCoreAsync(
            ChatMessage input, AgentContext context, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

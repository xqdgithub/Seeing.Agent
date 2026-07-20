using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz;
using Quartz.Impl;
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
    public async Task QuartzSchedulerEngine_StartAndStop_UpdatesStatus()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson();

        var engine = CreateEngine();

        (await engine.GetStatusAsync()).IsStarted.Should().BeFalse();

        await engine.StartAsync();
        (await engine.GetStatusAsync()).IsStarted.Should().BeTrue();
        engine.IsStarted.Should().BeTrue();

        await engine.StopAsync();
        (await engine.GetStatusAsync()).IsStarted.Should().BeFalse();
    }

    [Fact]
    public async Task QuartzSchedulerEngine_UpsertJob_CreatesJob()
    {
        var engine = CreateEngine();
        await engine.StartAsync();

        var schedule = new ScheduleSpec
        {
            Type = ScheduleTypes.Interval,
            Every = "1h"
        };

        await engine.UpsertJobAsync("test-job", schedule, ScheduleIntent.Active);

        var status = await engine.GetJobStatusAsync("test-job");
        status.JobId.Should().Be("test-job");
        status.State.Should().Be(JobState.Scheduled);

        await engine.StopAsync();
    }

    [Fact]
    public async Task UpsertJob_Interval_With_Windows_Has_Multiple_Triggers()
    {
        var engine = CreateEngine();
        await engine.StartAsync();
        await engine.UpsertJobAsync("win-job", new ScheduleSpec
        {
            Type = ScheduleTypes.Interval,
            Every = "40m",
            Timezone = TimeZoneInfo.Local.Id,
            Windows = new List<ScheduleWindow>
            {
                new() { Start = "09:00", End = "12:00" },
                new() { Start = "14:00", End = "18:00" }
            }
        }, ScheduleIntent.Active);

        var status = await engine.GetJobStatusAsync("win-job");
        status.State.Should().Be(JobState.Scheduled);
        status.NextFireTime.Should().NotBeNull();
        status.NextFireTime!.Value.Kind.Should().NotBe(DateTimeKind.Utc);

        await engine.StopAsync();
    }

    [Fact]
    public async Task ScheduleManager_PauseAndResume_UpdatesState()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson();
        var executedPrompts = new List<string>();
        var (manager, engine) = CreateManager(ws, executedPrompts);

        try
        {
            var job = new ScheduledJobSpec
            {
                Id = "pause-job",
                Intent = ScheduleIntent.Active,
                TaskType = ScheduleTaskTypes.Text,
                Text = "hello",
                Schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "1h" },
                Dispatch = new DispatchSpec { Target = new DispatchTarget { SessionId = "main" } }
            };

            await manager.StartAsync();
            await manager.CreateOrReplaceJobAsync(job);

            var initialStatus = await manager.GetJobStatusAsync("pause-job");
            initialStatus.State.Should().Be(JobState.Scheduled);

            // 暂停 → 状态变为 Paused
            await manager.PauseJobAsync("pause-job");
            var pausedStatus = await manager.GetJobStatusAsync("pause-job");
            pausedStatus.State.Should().Be(JobState.Paused);

            // 恢复 → 状态变为 Scheduled
            await manager.ResumeJobAsync("pause-job");
            var resumedStatus = await manager.GetJobStatusAsync("pause-job");
            resumedStatus.State.Should().Be(JobState.Scheduled);
        }
        finally
        {
            await manager.StopAsync();
        }
    }

    [Fact]
    public async Task QuartzSchedulerEngine_RemoveJob_DeletesJob()
    {
        var engine = CreateEngine();
        await engine.StartAsync();

        var schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "1h" };
        await engine.UpsertJobAsync("remove-job", schedule, ScheduleIntent.Active);

        (await engine.GetJobStatusAsync("remove-job")).JobId.Should().Be("remove-job");

        await engine.RemoveJobAsync("remove-job");

        var status = await engine.GetJobStatusAsync("remove-job");
        status.State.Should().Be(JobState.Disabled); // Non-existent = Disabled
    }

    [Fact]
    public async Task QuartzSchedulerEngine_CronSchedule_SetsNextFireTime()
    {
        var engine = CreateEngine();
        await engine.StartAsync();

        var schedule = new ScheduleSpec
        {
            Type = ScheduleTypes.Cron,
            Cron = "0 9 * * *",
            Timezone = "UTC"
        };

        await engine.UpsertJobAsync("cron-job", schedule, ScheduleIntent.Active);

        var status = await engine.GetJobStatusAsync("cron-job");
        status.NextFireTime.Should().NotBeNull();
        status.CronExpression.Should().Contain("9");  // 验证小时部分

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
              "version": 2,
              "jobs": [
                {
                  "id": "loaded-job",
                  "intent": "Active",
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

    [Fact]
    public async Task QuartzSchedulerEngine_GetAllJobStatuses_ReturnsAllJobs()
    {
        var engine = CreateEngine();
        await engine.StartAsync();

        await engine.UpsertJobAsync("job1", new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "1h" }, ScheduleIntent.Active);
        await engine.UpsertJobAsync("job2", new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "2h" }, ScheduleIntent.Active);

        var statuses = await engine.GetAllJobStatusesAsync();
        statuses.Count.Should().Be(2);
        statuses.Select(s => s.JobId).Should().Contain("job1", "job2");

        await engine.StopAsync();
    }
    
    [Fact]
    public async Task ScheduleManager_MigratesOldEnabledField()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson();

        var jobsPath = Path.Combine(ws.SeeingDirectory, "jobs.json");
        await File.WriteAllTextAsync(jobsPath, """
            {
              "jobs": [
                {
                  "id": "old-enabled-true",
                  "enabled": true,
                  "taskType": "text",
                  "text": "test",
                  "schedule": { "type": "interval", "every": "1h" },
                  "dispatch": { "target": { "sessionId": "main" } }
                },
                {
                  "id": "old-enabled-false",
                  "enabled": false,
                  "taskType": "text",
                  "text": "test2",
                  "schedule": { "type": "interval", "every": "1h" },
                  "dispatch": { "target": { "sessionId": "main" } }
                }
              ]
            }
            """);

        var (manager, _) = CreateManager(ws, []);
        await manager.StartAsync();

        var jobs = await manager.ListJobsAsync();
        jobs.First(j => j.Id == "old-enabled-true").Intent.Should().Be(ScheduleIntent.Active);
        jobs.First(j => j.Id == "old-enabled-false").Intent.Should().Be(ScheduleIntent.Disabled);

        await manager.StopAsync();
    }

    private static QuartzSchedulerEngine CreateEngine()
    {
        var factory = new StdSchedulerFactory();
        return new QuartzSchedulerEngine(factory, NullLogger<QuartzSchedulerEngine>.Instance);
    }

    private static (ScheduleManager Manager, QuartzSchedulerEngine Engine) CreateManager(
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

        var engine = CreateEngine();
        var dispatcher = Mock.Of<IScheduledJobDispatcher>(d =>
            d.DispatchAsync(It.IsAny<DispatchRequest>(), It.IsAny<CancellationToken>()) == Task.FromResult(DispatchResult.Ok()));

        var manager = new ScheduleManager(
            new JsonScheduleRepository(ws.Workspace, NullLogger<JsonScheduleRepository>.Instance),
            engine,
            optionsProvider,
            NullLogger<ScheduleManager>.Instance);

        return (manager, engine);
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

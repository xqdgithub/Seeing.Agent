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

        await engine.UpsertJobAsync("test-job", schedule, enabled: true);

        var status = await engine.GetJobStatusAsync("test-job");
        status.JobId.Should().Be("test-job");
        status.State.Should().Be(JobState.Normal);

        await engine.StopAsync();
    }

    [Fact]
    public async Task QuartzSchedulerEngine_PauseAndResume_UpdatesState()
    {
        var engine = CreateEngine();
        await engine.StartAsync();

        var schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "1h" };
        await engine.UpsertJobAsync("pause-job", schedule, enabled: true);

        await engine.PauseJobAsync("pause-job");
        var pausedStatus = await engine.GetJobStatusAsync("pause-job");
        pausedStatus.State.Should().Be(JobState.Paused);

        await engine.ResumeJobAsync("pause-job");
        var resumedStatus = await engine.GetJobStatusAsync("pause-job");
        resumedStatus.State.Should().Be(JobState.Normal);

        await engine.StopAsync();
    }

    [Fact]
    public async Task QuartzSchedulerEngine_RemoveJob_DeletesJob()
    {
        var engine = CreateEngine();
        await engine.StartAsync();

        var schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "1h" };
        await engine.UpsertJobAsync("remove-job", schedule, enabled: true);

        (await engine.GetJobStatusAsync("remove-job")).JobId.Should().Be("remove-job");

        await engine.RemoveJobAsync("remove-job");

        var status = await engine.GetJobStatusAsync("remove-job");
        status.State.Should().Be(JobState.Completed); // Non-existent = completed
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

        await engine.UpsertJobAsync("cron-job", schedule, enabled: true);

        var status = await engine.GetJobStatusAsync("cron-job");
        status.NextFireTime.Should().NotBeNull();
        status.CronExpression.Should().Be("0 9 * * *");

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

    [Fact]
    public async Task QuartzSchedulerEngine_GetAllJobStatuses_ReturnsAllJobs()
    {
        var engine = CreateEngine();
        await engine.StartAsync();

        await engine.UpsertJobAsync("job1", new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "1h" }, true);
        await engine.UpsertJobAsync("job2", new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "2h" }, true);

        var statuses = await engine.GetAllJobStatusesAsync();
        statuses.Count.Should().Be(2);
        statuses.Select(s => s.JobId).Should().Contain("job1", "job2");

        await engine.StopAsync();
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
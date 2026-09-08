using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Scheduler;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Scheduler.Persistence;
using Seeing.Agent.Scheduler.Tests.Fixtures;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

public class JsonScheduleRepositoryTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsJobs()
    {
        using var ws = new SchedulerTestWorkspace();
        var repo = new JsonScheduleRepository(ws.Workspace, NullLogger<JsonScheduleRepository>.Instance);

        var jobs = new JobsFile
        {
            Jobs =
            [
                new ScheduledJobSpec
                {
                    Id = "job-1",
                    Name = "daily-check",
                    TaskType = ScheduleTaskTypes.Agent,
                    Agent = "test-agent",
                    Prompt = "检查待办",
                    Schedule = new ScheduleSpec { Type = ScheduleTypes.Cron, Cron = "0 9 * * *" },
                    Dispatch = new DispatchSpec
                    {
                        Target = new DispatchTarget { SessionId = "main" }
                    }
                }
            ]
        };

        await repo.SaveAsync(jobs);
        var loaded = await repo.LoadAsync();

        loaded.Jobs.Should().HaveCount(1);
        loaded.Jobs[0].Id.Should().Be("job-1");
        loaded.Jobs[0].Prompt.Should().Be("检查待办");
        loaded.Jobs[0].Schedule.Cron.Should().Be("0 9 * * *");

        var jobsPath = Path.Combine(ws.SeeingDirectory, "jobs.json");
        File.Exists(jobsPath).Should().BeTrue();
    }

    [Fact]
    public async Task AppendHistory_RespectsMaxRecords()
    {
        using var ws = new SchedulerTestWorkspace();
        var repo = new JsonScheduleRepository(ws.Workspace, NullLogger<JsonScheduleRepository>.Instance);

        for (var i = 0; i < SchedulerConstants.MaxHistoryRecords + 5; i++)
        {
            await repo.AppendHistoryAsync("job-1", new JobExecutionRecord
            {
                RunId = $"run-{i}",
                Status = "success",
                Output = $"output-{i}"
            });
        }

        var history = await repo.GetHistoryAsync("job-1", 100);
        history.Should().HaveCount(SchedulerConstants.MaxHistoryRecords);
        history[0].RunId.Should().Be($"run-{SchedulerConstants.MaxHistoryRecords + 4}");
    }
}

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Scheduler.Tools;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests.Tools;

public class CronCreateToolTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void Id_ShouldBeCronCreate()
    {
        var tool = new CronCreateTool(NullLogger<CronCreateTool>.Instance, Mock.Of<IScheduleManager>());
        tool.Id.Should().Be("cron_create");
    }

    [Fact]
    public async Task MissingPrompt_ShouldFail()
    {
        var tool = new CronCreateTool(NullLogger<CronCreateTool>.Instance, Mock.Of<IScheduleManager>());
        var result = await tool.ExecuteAsync(
            Args(new { schedule = new { type = "cron", cron = "0 9 * * *" } }),
            new ToolContext { SessionId = "sess-1" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("prompt");
    }

    [Fact]
    public async Task MissingSchedule_ShouldFail()
    {
        var tool = new CronCreateTool(NullLogger<CronCreateTool>.Instance, Mock.Of<IScheduleManager>());
        var result = await tool.ExecuteAsync(
            Args(new { prompt = "do work" }),
            new ToolContext { SessionId = "sess-1" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("schedule");
    }

    [Fact]
    public async Task InvalidSchedule_ShouldFailWithExamples()
    {
        var tool = new CronCreateTool(NullLogger<CronCreateTool>.Instance, Mock.Of<IScheduleManager>());
        var result = await tool.ExecuteAsync(
            Args(new { prompt = "do work", schedule = new { type = "cron", cron = "not-a-cron" } }),
            new ToolContext { SessionId = "sess-1" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("示例");
        result.Error.Should().MatchRegex("(?i)cron|interval|once");
    }

    [Fact]
    public async Task ValidCron_ShouldCreateAgentJobWithSessionIdFromContext()
    {
        ScheduledJobSpec? captured = null;
        var manager = new Mock<IScheduleManager>();
        manager.Setup(m => m.CreateOrReplaceJobAsync(It.IsAny<ScheduledJobSpec>(), It.IsAny<CancellationToken>()))
            .Callback<ScheduledJobSpec, CancellationToken>((job, _) => captured = job)
            .ReturnsAsync((ScheduledJobSpec job, CancellationToken _) => job);

        var tool = new CronCreateTool(NullLogger<CronCreateTool>.Instance, manager.Object);
        var result = await tool.ExecuteAsync(
            Args(new
            {
                id = "my-job",
                name = "Morning",
                prompt = "summarize inbox",
                agent = "sisyphus",
                schedule = new { type = "cron", cron = "0 9 * * *", timezone = "UTC" }
            }),
            new ToolContext { SessionId = "sess-from-context" });

        result.Success.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.Id.Should().Be("my-job");
        captured.Name.Should().Be("Morning");
        captured.Prompt.Should().Be("summarize inbox");
        captured.Agent.Should().Be("sisyphus");
        captured.TaskType.Should().Be(ScheduleTaskTypes.Agent);
        captured.Intent.Should().Be(ScheduleIntent.Active);
        captured.Schedule.Type.Should().Be(ScheduleTypes.Cron);
        captured.Schedule.Cron.Should().Be("0 9 * * *");
        captured.Dispatch.Target.SessionId.Should().Be("sess-from-context");
        manager.Verify(m => m.CreateOrReplaceJobAsync(It.IsAny<ScheduledJobSpec>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MissingId_ShouldGenerateJobPrefixedId()
    {
        ScheduledJobSpec? captured = null;
        var manager = new Mock<IScheduleManager>();
        manager.Setup(m => m.CreateOrReplaceJobAsync(It.IsAny<ScheduledJobSpec>(), It.IsAny<CancellationToken>()))
            .Callback<ScheduledJobSpec, CancellationToken>((job, _) => captured = job)
            .ReturnsAsync((ScheduledJobSpec job, CancellationToken _) => job);

        var tool = new CronCreateTool(NullLogger<CronCreateTool>.Instance, manager.Object);
        var result = await tool.ExecuteAsync(
            Args(new
            {
                prompt = "ping",
                schedule = new { type = "interval", every = "30m" }
            }),
            new ToolContext { SessionId = "s1" });

        result.Success.Should().BeTrue();
        captured!.Id.Should().StartWith("job_");
        captured.TaskType.Should().Be(ScheduleTaskTypes.Agent);
        captured.Dispatch.Target.SessionId.Should().Be("s1");
    }

    [Fact]
    public async Task OnceSchedule_ShouldAcceptRunAt()
    {
        ScheduledJobSpec? captured = null;
        var manager = new Mock<IScheduleManager>();
        manager.Setup(m => m.CreateOrReplaceJobAsync(It.IsAny<ScheduledJobSpec>(), It.IsAny<CancellationToken>()))
            .Callback<ScheduledJobSpec, CancellationToken>((job, _) => captured = job)
            .ReturnsAsync((ScheduledJobSpec job, CancellationToken _) => job);

        var tool = new CronCreateTool(NullLogger<CronCreateTool>.Instance, manager.Object);
        var result = await tool.ExecuteAsync(
            Args(new
            {
                prompt = "once",
                schedule = new { type = "once", runAt = "2026-12-01T10:00:00" }
            }),
            new ToolContext { SessionId = "s1" });

        result.Success.Should().BeTrue();
        captured!.Schedule.Type.Should().Be(ScheduleTypes.Once);
        captured.Schedule.RunAt.Should().NotBeNull();
    }
}

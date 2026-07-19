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

public class CronControlToolsTests
{
    private static readonly JsonElement EmptyArgs = JsonSerializer.SerializeToElement(new { });

    private static JsonElement ArgsWithId(string id) =>
        JsonSerializer.SerializeToElement(new { id });

    [Fact]
    public void CronListTool_Id_ShouldBeCronList()
    {
        var tool = new CronListTool(NullLogger<CronListTool>.Instance, Mock.Of<IScheduleManager>());
        tool.Id.Should().Be("cron_list");
    }

    [Fact]
    public async Task CronList_Empty_ShouldReturnFriendlyMessage()
    {
        var manager = new Mock<IScheduleManager>();
        manager.Setup(m => m.ListJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ScheduledJobSpec>());

        var tool = new CronListTool(NullLogger<CronListTool>.Instance, manager.Object);
        var result = await tool.ExecuteAsync(EmptyArgs, new ToolContext());

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("没有");
    }

    [Fact]
    public async Task CronList_WithJobs_ShouldIncludeIntentAndNextFire()
    {
        var manager = new Mock<IScheduleManager>();
        manager.Setup(m => m.ListJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ScheduledJobSpec
                {
                    Id = "job-1",
                    TaskType = ScheduleTaskTypes.Agent,
                    Intent = ScheduleIntent.Active,
                    Agent = "sisyphus"
                }
            });
        manager.Setup(m => m.GetJobStatusAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JobStatus
            {
                JobId = "job-1",
                NextFireTime = new DateTime(2026, 7, 20, 10, 30, 0)
            });

        var tool = new CronListTool(NullLogger<CronListTool>.Instance, manager.Object);
        var result = await tool.ExecuteAsync(EmptyArgs, new ToolContext());

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("job-1");
        result.Output.Should().Contain("Active");
        result.Output.Should().Contain("2026-07-20 10:30:00");
    }

    [Fact]
    public async Task CronRun_MissingId_ShouldFail()
    {
        var tool = new CronRunTool(NullLogger<CronRunTool>.Instance, Mock.Of<IScheduleManager>());
        var result = await tool.ExecuteAsync(EmptyArgs, new ToolContext());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("id");
    }

    [Fact]
    public async Task CronRun_Accepted_ShouldSucceed()
    {
        var manager = new Mock<IScheduleManager>();
        manager.Setup(m => m.RunJobOnceAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TriggerResult.Accepted("run-abc"));

        var tool = new CronRunTool(NullLogger<CronRunTool>.Instance, manager.Object);
        var result = await tool.ExecuteAsync(ArgsWithId("job-1"), new ToolContext());

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("run-abc");
    }

    [Theory]
    [InlineData(nameof(TriggerResult.NotFound))]
    [InlineData(nameof(TriggerResult.Disabled))]
    public async Task CronRun_NotFoundOrDisabled_ShouldFail(string kind)
    {
        TriggerResult trigger = kind switch
        {
            nameof(TriggerResult.NotFound) => new TriggerResult.NotFound(),
            _ => new TriggerResult.Disabled()
        };

        var manager = new Mock<IScheduleManager>();
        manager.Setup(m => m.RunJobOnceAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(trigger);

        var tool = new CronRunTool(NullLogger<CronRunTool>.Instance, manager.Object);
        var result = await tool.ExecuteAsync(ArgsWithId("job-1"), new ToolContext());

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task CronRun_Conflict_ShouldFailWithReason()
    {
        var manager = new Mock<IScheduleManager>();
        manager.Setup(m => m.RunJobOnceAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TriggerResult.Conflict("任务正在执行中"));

        var tool = new CronRunTool(NullLogger<CronRunTool>.Instance, manager.Object);
        var result = await tool.ExecuteAsync(ArgsWithId("job-1"), new ToolContext());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("任务正在执行中");
    }

    [Fact]
    public async Task CronDisable_MissingJob_ShouldFailWithoutCallingDisable()
    {
        var manager = new Mock<IScheduleManager>();
        manager.Setup(m => m.GetJobAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ScheduledJobSpec?)null);

        var tool = new CronDisableTool(NullLogger<CronDisableTool>.Instance, manager.Object);
        var result = await tool.ExecuteAsync(ArgsWithId("missing"), new ToolContext());

        result.Success.Should().BeFalse();
        manager.Verify(m => m.DisableJobAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CronDisable_ExistingJob_ShouldDisable()
    {
        var manager = new Mock<IScheduleManager>();
        manager.Setup(m => m.GetJobAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScheduledJobSpec { Id = "job-1" });
        manager.Setup(m => m.DisableJobAsync("job-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var tool = new CronDisableTool(NullLogger<CronDisableTool>.Instance, manager.Object);
        var result = await tool.ExecuteAsync(ArgsWithId("job-1"), new ToolContext());

        result.Success.Should().BeTrue();
        manager.Verify(m => m.DisableJobAsync("job-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CronResume_MissingJob_ShouldFailWithoutCallingResume()
    {
        var manager = new Mock<IScheduleManager>();
        manager.Setup(m => m.GetJobAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ScheduledJobSpec?)null);

        var tool = new CronResumeTool(NullLogger<CronResumeTool>.Instance, manager.Object);
        var result = await tool.ExecuteAsync(ArgsWithId("missing"), new ToolContext());

        result.Success.Should().BeFalse();
        manager.Verify(m => m.ResumeJobAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CronResume_ExistingJob_ShouldResume()
    {
        var manager = new Mock<IScheduleManager>();
        manager.Setup(m => m.GetJobAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScheduledJobSpec { Id = "job-1" });
        manager.Setup(m => m.ResumeJobAsync("job-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var tool = new CronResumeTool(NullLogger<CronResumeTool>.Instance, manager.Object);
        var result = await tool.ExecuteAsync(ArgsWithId("job-1"), new ToolContext());

        result.Success.Should().BeTrue();
        manager.Verify(m => m.ResumeJobAsync("job-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CronDelete_True_ShouldSucceed()
    {
        var manager = new Mock<IScheduleManager>();
        manager.Setup(m => m.DeleteJobAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var tool = new CronDeleteTool(NullLogger<CronDeleteTool>.Instance, manager.Object);
        var result = await tool.ExecuteAsync(ArgsWithId("job-1"), new ToolContext());

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task CronDelete_False_ShouldFail()
    {
        var manager = new Mock<IScheduleManager>();
        manager.Setup(m => m.DeleteJobAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var tool = new CronDeleteTool(NullLogger<CronDeleteTool>.Instance, manager.Object);
        var result = await tool.ExecuteAsync(ArgsWithId("missing"), new ToolContext());

        result.Success.Should().BeFalse();
    }

    [Fact]
    public void ToolIds_ShouldMatchExpected()
    {
        new CronRunTool(NullLogger<CronRunTool>.Instance, Mock.Of<IScheduleManager>()).Id.Should().Be("cron_run");
        new CronDisableTool(NullLogger<CronDisableTool>.Instance, Mock.Of<IScheduleManager>()).Id.Should().Be("cron_disable");
        new CronResumeTool(NullLogger<CronResumeTool>.Instance, Mock.Of<IScheduleManager>()).Id.Should().Be("cron_resume");
        new CronDeleteTool(NullLogger<CronDeleteTool>.Instance, Mock.Of<IScheduleManager>()).Id.Should().Be("cron_delete");
    }
}

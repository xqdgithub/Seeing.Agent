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

/// <summary>
/// Light integration smoke: create → list → disable → resume → delete via cron tools + Moq IScheduleManager.
/// </summary>
public class CronToolsLifecycleSmokeTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task CreateListDisableResumeDelete_ShouldSucceedEndToEnd()
    {
        ScheduledJobSpec? stored = null;
        var disabled = false;

        var manager = new Mock<IScheduleManager>();
        manager.Setup(m => m.CreateOrReplaceJobAsync(It.IsAny<ScheduledJobSpec>(), It.IsAny<CancellationToken>()))
            .Callback<ScheduledJobSpec, CancellationToken>((job, _) =>
            {
                stored = job;
                disabled = false;
            })
            .ReturnsAsync((ScheduledJobSpec job, CancellationToken _) => job);

        manager.Setup(m => m.ListJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => stored is null ? Array.Empty<ScheduledJobSpec>() : new[] { stored });

        manager.Setup(m => m.GetJobStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => new JobStatus
            {
                JobId = id,
                State = disabled ? JobState.Disabled : JobState.Scheduled,
                NextFireTime = DateTime.UtcNow.AddHours(1)
            });

        manager.Setup(m => m.GetJobAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => stored);

        manager.Setup(m => m.DisableJobAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => disabled = true)
            .Returns(Task.CompletedTask);

        manager.Setup(m => m.ResumeJobAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => disabled = false)
            .Returns(Task.CompletedTask);

        manager.Setup(m => m.DeleteJobAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) =>
            {
                if (stored?.Id != id)
                    return false;
                stored = null;
                return true;
            });

        var create = new CronCreateTool(NullLogger<CronCreateTool>.Instance, manager.Object);
        var list = new CronListTool(NullLogger<CronListTool>.Instance, manager.Object);
        var disable = new CronDisableTool(NullLogger<CronDisableTool>.Instance, manager.Object);
        var resume = new CronResumeTool(NullLogger<CronResumeTool>.Instance, manager.Object);
        var delete = new CronDeleteTool(NullLogger<CronDeleteTool>.Instance, manager.Object);
        var ctx = new ToolContext { SessionId = "smoke-session" };

        var created = await create.ExecuteAsync(
            Args(new
            {
                id = "smoke-job",
                prompt = "lifecycle smoke",
                schedule = new { type = "cron", cron = "0 9 * * *" }
            }),
            ctx);
        created.Success.Should().BeTrue();
        stored!.Dispatch.Target.SessionId.Should().Be("smoke-session");

        var listed = await list.ExecuteAsync(Args(new { }), ctx);
        listed.Success.Should().BeTrue();
        listed.Output.Should().Contain("smoke-job");

        (await disable.ExecuteAsync(Args(new { id = "smoke-job" }), ctx)).Success.Should().BeTrue();
        disabled.Should().BeTrue();

        (await resume.ExecuteAsync(Args(new { id = "smoke-job" }), ctx)).Success.Should().BeTrue();
        disabled.Should().BeFalse();

        (await delete.ExecuteAsync(Args(new { id = "smoke-job" }), ctx)).Success.Should().BeTrue();
        stored.Should().BeNull();

        var afterDelete = await list.ExecuteAsync(Args(new { }), ctx);
        afterDelete.Success.Should().BeTrue();
        afterDelete.Output.Should().Contain("没有");
    }
}

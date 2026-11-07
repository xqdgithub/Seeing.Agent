using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Scheduler.Tools;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests.Tools;

public class HeartbeatToolsTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static HeartbeatSnapshot Snapshot() => new(
        new ScheduledJobSpec
        {
            Id = SchedulerConstants.HeartbeatJobId,
            Name = "心跳",
            Intent = ScheduleIntent.Active,
            TaskType = ScheduleTaskTypes.Agent,
            Prompt = "检查待办",
            Schedule = new ScheduleSpec
            {
                Type = ScheduleTypes.Interval,
                Every = "6h",
                Timezone = "Asia/Shanghai"
            },
            Dispatch = new DispatchSpec { Target = new DispatchTarget { SessionId = "main" } }
        },
        new JobStatus
        {
            JobId = SchedulerConstants.HeartbeatJobId,
            State = JobState.Scheduled,
            NextFireTime = new DateTime(2026, 11, 7, 18, 0, 0)
        },
        new JobExecutionRecord
        {
            JobId = SchedulerConstants.HeartbeatJobId,
            Status = "success",
            StartedAt = new DateTime(2026, 11, 7, 12, 0, 0),
            CompletedAt = new DateTime(2026, 11, 7, 12, 0, 7),
            Source = ScheduleSources.Heartbeat
        },
        SchedulerStarted: true);

    [Fact]
    public async Task Get_ShouldDescribeConfigNextFireAndLastRun()
    {
        var service = new Mock<IHeartbeatService>();
        service.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Snapshot());

        var tool = new HeartbeatGetTool(NullLogger<HeartbeatGetTool>.Instance, service.Object);
        var result = await tool.ExecuteAsync(Args(new { }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeTrue(result.Error);
        result.Output.Should().Contain("6h");
        result.Output.Should().Contain("2026-11-07 18:00:00");
        result.Output.Should().Contain("成功");
        result.Output.Should().Contain("检查待办");
    }

    [Fact]
    public async Task Get_WhenNotConfigured_SaysSoAndPointsToUpdate()
    {
        var service = new Mock<IHeartbeatService>();
        service.Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HeartbeatSnapshot(null, null, null, true));

        var tool = new HeartbeatGetTool(NullLogger<HeartbeatGetTool>.Instance, service.Object);
        var result = await tool.ExecuteAsync(Args(new { }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeTrue(result.Error);
        result.Output.Should().Contain("未配置");
        result.Output.Should().Contain("heartbeat_update");
    }

    [Fact]
    public async Task Get_WhenSchedulerNotStarted_SaysItWillNotFire()
    {
        var service = new Mock<IHeartbeatService>();
        service.Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot() with { SchedulerStarted = false });

        var tool = new HeartbeatGetTool(NullLogger<HeartbeatGetTool>.Instance, service.Object);
        var result = await tool.ExecuteAsync(Args(new { }), new ToolContext { SessionId = "s1" });

        result.Output.Should().Contain("未启动");
    }

    [Fact]
    public async Task Update_ShouldOnlyForwardProvidedFields()
    {
        HeartbeatUpdate? captured = null;
        var service = new Mock<IHeartbeatService>();
        service.Setup(s => s.UpdateAsync(It.IsAny<HeartbeatUpdate>(), It.IsAny<CancellationToken>()))
            .Callback<HeartbeatUpdate, CancellationToken>((u, _) => captured = u)
            .ReturnsAsync(Snapshot());

        var tool = new HeartbeatUpdateTool(NullLogger<HeartbeatUpdateTool>.Instance, service.Object);
        var result = await tool.ExecuteAsync(
            Args(new { enabled = false }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeTrue(result.Error);
        captured.Should().NotBeNull();
        captured!.Enabled.Should().BeFalse();
        captured.Prompt.Should().BeNull();
        captured.Every.Should().BeNull();
        captured.Agent.Should().BeNull();
        captured.Windows.Should().BeNull();
        captured.Conversation.Should().BeNull();
    }

    [Fact]
    public async Task Update_ShouldForwardWindowsAndConversation()
    {
        HeartbeatUpdate? captured = null;
        var service = new Mock<IHeartbeatService>();
        service.Setup(s => s.UpdateAsync(It.IsAny<HeartbeatUpdate>(), It.IsAny<CancellationToken>()))
            .Callback<HeartbeatUpdate, CancellationToken>((u, _) => captured = u)
            .ReturnsAsync(Snapshot());

        var tool = new HeartbeatUpdateTool(NullLogger<HeartbeatUpdateTool>.Instance, service.Object);
        await tool.ExecuteAsync(Args(new
        {
            windows = new[] { new { start = "08:00", end = "22:00" } },
            conversation = new { channelId = "qq", kind = "group", externalId = "123" }
        }), new ToolContext { SessionId = "s1" });

        captured.Should().NotBeNull();
        captured!.Windows.Should().HaveCount(1);
        captured.Windows![0].Start.Should().Be("08:00");
        captured.Conversation.Should().NotBeNull();
        captured.Conversation!.ChannelId.Should().Be("qq");
        captured.Conversation.ExternalId.Should().Be("123");
    }

    [Fact]
    public async Task Update_WhenValidationFails_ReturnsFailure()
    {
        var service = new Mock<IHeartbeatService>();
        service.Setup(s => s.UpdateAsync(It.IsAny<HeartbeatUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("心跳间隔格式无效"));

        var tool = new HeartbeatUpdateTool(NullLogger<HeartbeatUpdateTool>.Instance, service.Object);
        var result = await tool.ExecuteAsync(
            Args(new { every = "6 hours" }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("心跳间隔格式无效");
    }

    [Fact]
    public async Task Run_Accepted_ReturnsRunId()
    {
        var service = new Mock<IHeartbeatService>();
        service.Setup(s => s.RunNowAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TriggerResult.Accepted("run-1"));

        var result = await new HeartbeatRunTool(NullLogger<HeartbeatRunTool>.Instance, service.Object)
            .ExecuteAsync(Args(new { }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeTrue(result.Error);
        result.Output.Should().Contain("run-1");
    }

    [Fact]
    public async Task Run_NotFound_SaysNotConfigured()
    {
        var service = new Mock<IHeartbeatService>();
        service.Setup(s => s.RunNowAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TriggerResult.NotFound());

        var result = await new HeartbeatRunTool(NullLogger<HeartbeatRunTool>.Instance, service.Object)
            .ExecuteAsync(Args(new { }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("未配置");
    }

    [Fact]
    public async Task Run_Disabled_SaysNotEnabled()
    {
        var service = new Mock<IHeartbeatService>();
        service.Setup(s => s.RunNowAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TriggerResult.Disabled());

        var result = await new HeartbeatRunTool(NullLogger<HeartbeatRunTool>.Instance, service.Object)
            .ExecuteAsync(Args(new { }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("未启用");
    }

    [Fact]
    public async Task Run_Conflict_SaysAlreadyRunning()
    {
        var service = new Mock<IHeartbeatService>();
        service.Setup(s => s.RunNowAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TriggerResult.Conflict("任务正在执行中"));

        var result = await new HeartbeatRunTool(NullLogger<HeartbeatRunTool>.Instance, service.Object)
            .ExecuteAsync(Args(new { }), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("正在执行");
    }
}

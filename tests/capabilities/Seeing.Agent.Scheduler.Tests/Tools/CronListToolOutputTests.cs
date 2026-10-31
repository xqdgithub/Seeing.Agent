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

/// <summary>cron_list 必须回显任务的具体内容，而不是只有 id 与状态。</summary>
public class CronListToolOutputTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task List_ShouldIncludeNameScheduleAndContent()
    {
        var job = new ScheduledJobSpec
        {
            Id = "job_lunch",
            Name = "午间提醒",
            TaskType = ScheduleTaskTypes.Text,
            Text = "记得吃午饭",
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec
            {
                Type = ScheduleTypes.Cron,
                Cron = "0 12 * * *",
                Timezone = "Asia/Shanghai"
            },
            Dispatch = new DispatchSpec { Target = new DispatchTarget { SessionId = "ses_1" } }
        };

        var manager = new Mock<IScheduleManager>();
        manager.Setup(m => m.ListJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { job });
        manager.Setup(m => m.GetJobStatusAsync("job_lunch", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JobStatus
            {
                JobId = "job_lunch",
                State = JobState.Scheduled,
                NextFireTime = new DateTime(2026, 10, 31, 12, 0, 0)
            });

        var tool = new CronListTool(NullLogger<CronListTool>.Instance, manager.Object);
        var result = await tool.ExecuteAsync(Args(new { }), new ToolContext { SessionId = "ses_1" });

        result.Success.Should().BeTrue(result.Error);
        result.Output.Should().Contain("job_lunch");
        result.Output.Should().Contain("午间提醒");
        result.Output.Should().Contain("0 12 * * *");
        result.Output.Should().Contain("记得吃午饭");
        result.Output.Should().Contain("2026-10-31 12:00:00");
        result.Output.Should().Contain("text");
        result.Output.Should().Contain("ses_1");
    }

    [Fact]
    public async Task List_ShouldShowExplicitConversationTarget()
    {
        var job = new ScheduledJobSpec
        {
            Id = "job_group",
            Name = "研发群日报",
            TaskType = ScheduleTaskTypes.Text,
            Text = "该交日报了",
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Cron, Cron = "0 18 * * *", Timezone = "Asia/Shanghai" },
            Dispatch = new DispatchSpec
            {
                Target = new DispatchTarget
                {
                    SessionId = "ses_3",
                    Conversation = new ConversationSpec
                    {
                        ChannelId = "qq",
                        Kind = "group",
                        ExternalId = "987654",
                        Title = "研发群"
                    }
                }
            }
        };

        var manager = new Mock<IScheduleManager>();
        manager.Setup(m => m.ListJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { job });
        manager.Setup(m => m.GetJobStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("scheduler not started"));

        var tool = new CronListTool(NullLogger<CronListTool>.Instance, manager.Object);
        var result = await tool.ExecuteAsync(Args(new { }), new ToolContext { SessionId = "ses_3" });

        result.Success.Should().BeTrue(result.Error);
        result.Output.Should().Contain("出站目标");
        result.Output.Should().Contain("qq/group/987654");
    }

    [Fact]
    public async Task List_AgentJob_ShouldIncludeAgentAndPrompt()
    {
        var job = new ScheduledJobSpec
        {
            Id = "job_agent",
            TaskType = ScheduleTaskTypes.Agent,
            Prompt = "汇总未读邮件",
            Agent = "build",
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "6h", Timezone = "Asia/Shanghai" },
            Dispatch = new DispatchSpec { Target = new DispatchTarget { SessionId = "ses_2" } }
        };

        var manager = new Mock<IScheduleManager>();
        manager.Setup(m => m.ListJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { job });
        manager.Setup(m => m.GetJobStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("scheduler not started"));

        var tool = new CronListTool(NullLogger<CronListTool>.Instance, manager.Object);
        var result = await tool.ExecuteAsync(Args(new { }), new ToolContext { SessionId = "ses_2" });

        result.Success.Should().BeTrue(result.Error);
        result.Output.Should().Contain("汇总未读邮件");
        result.Output.Should().Contain("build");
        result.Output.Should().Contain("6h");
    }
}

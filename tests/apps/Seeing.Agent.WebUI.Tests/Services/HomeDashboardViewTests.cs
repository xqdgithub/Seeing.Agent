using FluentAssertions;
using Seeing.Agent.Scheduler;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>主页仪表盘投影逻辑（相对时间、空卡判定、即将触发、过期计数）。</summary>
public class HomeDashboardViewTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 15, 0, 0);

    [Theory]
    [InlineData(0, "刚刚")]
    [InlineData(3, "3 分钟前")]
    [InlineData(120, "2 小时前")]
    [InlineData(60 * 26, "昨天")]
    [InlineData(60 * 24 * 4, "4 天前")]
    public void RelativeTime_ShouldBucketByElapsed(int minutesAgo, string expected)
    {
        HomeDashboardView.RelativeTime(Now.AddMinutes(-minutesAgo), Now).Should().Be(expected);
    }

    [Fact]
    public void RelativeTime_Future_ShouldBeJustNow()
    {
        HomeDashboardView.RelativeTime(Now.AddMinutes(5), Now).Should().Be("刚刚");
    }

    [Fact]
    public void RelativeTime_WhenTimeIsUtc_ShouldNormalizeKinds()
    {
        // SessionSummary.LastActiveAt 写入路径 Kind 不一致（Utc / Local 混用），
        // 直接相减会差出一个时区偏移：这里的 UTC 时间代表「2 分钟前」，必须仍然显示 2 分钟前。
        var localNow = new DateTime(2026, 10, 31, 15, 0, 0, DateTimeKind.Local);
        var utcTime = localNow.ToUniversalTime().AddMinutes(-2);

        HomeDashboardView.RelativeTime(utcTime, localNow).Should().Be("2 分钟前");
    }

    [Theory]
    [InlineData(0, 0, 0, false)]
    [InlineData(1, 0, 0, true)]
    [InlineData(0, 2, 0, true)]
    [InlineData(0, 0, 3, true)]
    public void HasActionItems_ShouldBeTrueOnlyWhenAnyNonZero(int approvals, int questions, int failures, bool expected)
    {
        HomeDashboardView.HasActionItems(approvals, questions, failures).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, "未命名会话")]
    [InlineData("   ", "未命名会话")]
    [InlineData("跑测试", "跑测试")]
    public void SessionTitle_ShouldFallbackWhenBlank(string? title, string expected)
    {
        HomeDashboardView.SessionTitle(title).Should().Be(expected);
    }

    [Fact]
    public void UpcomingRuns_ShouldSortAscendingSkipExpiredAndDisabled()
    {
        var cron = new ScheduledJobSpec
        {
            Id = "j1",
            Name = "起床提醒",
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Cron, Cron = "0 8 * * *", Timezone = "Asia/Shanghai" }
        };
        var expiredOnce = new ScheduledJobSpec
        {
            Id = "j2",
            Name = "过期一次性",
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Once, RunAt = Now.AddHours(-1), Timezone = "Asia/Shanghai" }
        };
        var disabled = new ScheduledJobSpec
        {
            Id = "j3",
            Name = "已禁用",
            Intent = ScheduleIntent.Disabled,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Cron, Cron = "0 9 * * *", Timezone = "Asia/Shanghai" }
        };

        var statuses = new Dictionary<string, JobStatus>
        {
            ["j1"] = new() { JobId = "j1", NextFireTime = Now.AddHours(2) },
            ["j2"] = new() { JobId = "j2" },
            ["j3"] = new() { JobId = "j3", NextFireTime = Now.AddHours(1) }
        };

        var runs = HomeDashboardView.UpcomingRuns([cron, expiredOnce, disabled], statuses, Now, take: 3);

        runs.Should().HaveCount(1);
        runs[0].JobId.Should().Be("j1");
        runs[0].Title.Should().Be("起床提醒");
        runs[0].When.Should().Be(Now.AddHours(2));
    }

    [Fact]
    public void UpcomingRuns_WhenStatusMissing_ShouldComputeFromSchedule()
    {
        var once = new ScheduledJobSpec
        {
            Id = "j4",
            Name = "开会",
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Once, RunAt = Now.AddHours(3), Timezone = "Asia/Shanghai" }
        };

        var runs = HomeDashboardView.UpcomingRuns([once], new Dictionary<string, JobStatus>(), Now, take: 1);

        runs.Should().HaveCount(1);
        runs[0].When.Should().Be(Now.AddHours(3));
    }

    [Fact]
    public void UpcomingRuns_ShouldRespectTake()
    {
        var jobs = Enumerable.Range(0, 5).Select(i => new ScheduledJobSpec
        {
            Id = $"j{i}",
            Name = $"任务 {i}",
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "1h", Timezone = "Asia/Shanghai" }
        }).ToList();

        var statuses = jobs.ToDictionary(j => j.Id, j => new JobStatus { JobId = j.Id, NextFireTime = Now.AddHours(1) });

        HomeDashboardView.UpcomingRuns(jobs, statuses, Now, take: 3).Should().HaveCount(3);
    }

    [Fact]
    public void UpcomingRuns_InvalidScheduleExpression_ShouldSkipInsteadOfThrow()
    {
        // 表达式非法时 GetNextOccurrence 会抛异常；首页不能因此整卡渲染失败（应跳过该任务）。
        var job = new ScheduledJobSpec
        {
            Id = "j7",
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "not-an-interval" }
        };

        var runs = HomeDashboardView.UpcomingRuns([job], new Dictionary<string, JobStatus>(), Now, take: 3);

        runs.Should().BeEmpty();
    }

    [Fact]
    public void CountExpired_ShouldFollowJobExpiryRules()
    {
        var once = new ScheduledJobSpec
        {
            Id = "j5",
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Once, RunAt = Now.AddMinutes(-5) }
        };

        HomeDashboardView.CountExpired([once], new Dictionary<string, JobStatus>(), Now, schedulerStarted: false)
            .Should().Be(1, "一次性任务的 runAt 已过去，与调度器是否启动无关");
    }

    [Fact]
    public void CountExpired_SchedulerNotStarted_ShouldNotFlagCronJobs()
    {
        var cron = new ScheduledJobSpec
        {
            Id = "j6",
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Cron, Cron = "0 8 * * *" }
        };
        var statuses = new Dictionary<string, JobStatus>
        {
            ["j6"] = new() { JobId = "j6", PreviousFireTime = Now.AddHours(-1), NextFireTime = null }
        };

        HomeDashboardView.CountExpired([cron], statuses, Now, schedulerStarted: false)
            .Should().Be(0, "调度器未启动时状态不可信，不得判过期");
    }

    [Fact]
    public void UpcomingRuns_TitleShouldFallbackToIdWhenNameMissing()
    {
        var job = new ScheduledJobSpec
        {
            Id = "job_abc",
            Name = null,
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Once, RunAt = Now.AddHours(1) }
        };

        HomeDashboardView.UpcomingRuns([job], new Dictionary<string, JobStatus>(), Now, take: 1)
            .Single().Title.Should().Be("job_abc");
    }
}

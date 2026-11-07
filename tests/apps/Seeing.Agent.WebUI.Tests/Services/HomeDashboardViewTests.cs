using FluentAssertions;
using Seeing.Agent.Scheduler;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.WebUI.Services;

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
    public void TodayRuns_InvalidScheduleExpression_ShouldSkipInsteadOfThrow()
    {
        // 表达式非法时 GetNextOccurrence 会抛异常；首页不能因此整卡渲染失败（应跳过该任务）。
        var job = new ScheduledJobSpec
        {
            Id = "j7",
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "not-an-interval" }
        };

        HomeDashboardView.TodayRuns([job], new Dictionary<string, JobStatus>(), Now).Should().BeEmpty();
    }

    [Fact]
    public void TodayRuns_ShouldIncludeWholeDayOfDailyCron()
    {
        var cron = new ScheduledJobSpec
        {
            Id = "daily",
            Name = "起床提醒",
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Cron, Cron = "0 9 * * *", Timezone = "Asia/Shanghai" }
        };

        var runs = HomeDashboardView.TodayRuns([cron], new Dictionary<string, JobStatus>(), Now, perJobLimit: 10);

        runs.Should().ContainSingle();
        runs[0].When.Should().Be(Now.Date.AddHours(9), "当天 09:00 的触发点即使已过去也要出现在时间轴上");
        runs[0].Title.Should().Be("起床提醒");
    }

    [Fact]
    public void TodayRuns_ShouldEnumerateHourlyIntervalWithinDay()
    {
        var hourly = new ScheduledJobSpec
        {
            Id = "hourly",
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "1h", Timezone = "Asia/Shanghai" }
        };

        var runs = HomeDashboardView.TodayRuns([hourly], new Dictionary<string, JobStatus>(), Now, perJobLimit: 60);

        runs.Should().HaveCount(23, "01:00 到 23:00，不含次日 00:00");
        runs[0].When.Should().Be(Now.Date.AddHours(1));
        runs[^1].When.Should().Be(Now.Date.AddHours(23));
    }

    [Fact]
    public void TodayRuns_ShouldRespectPerJobLimit()
    {
        var hourly = new ScheduledJobSpec
        {
            Id = "hourly",
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "1h", Timezone = "Asia/Shanghai" }
        };

        HomeDashboardView.TodayRuns([hourly], new Dictionary<string, JobStatus>(), Now, perJobLimit: 5)
            .Should().HaveCount(5);
    }

    [Fact]
    public void TodayRuns_ShouldSkipExpiredAndDisabledJobs()
    {
        var expiredOnce = new ScheduledJobSpec
        {
            Id = "past",
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Once, RunAt = Now.AddHours(-1) }
        };
        var disabled = new ScheduledJobSpec
        {
            Id = "off",
            Intent = ScheduleIntent.Disabled,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Once, RunAt = Now.AddHours(2) }
        };

        // 时间轴只铺「还会触发」的任务：任务本身已作废（以后不再跑）不该占位，
        // 否则会让人误以为今天还有安排。「今日已执行」走执行历史，见 TodayExecutions。
        HomeDashboardView.TodayRuns([expiredOnce, disabled], new Dictionary<string, JobStatus>(), Now, perJobLimit: 5)
            .Should().BeEmpty();
    }

    [Fact]
    public void TodayRuns_ShouldPlaceOnceJobOnTodayAndIgnoreTomorrow()
    {
        var today = new ScheduledJobSpec
        {
            Id = "today",
            Name = "今天开会",
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Once, RunAt = Now.Date.AddHours(18) }
        };
        var tomorrow = new ScheduledJobSpec
        {
            Id = "tomorrow",
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Once, RunAt = Now.Date.AddDays(1).AddHours(9) }
        };

        var runs = HomeDashboardView.TodayRuns([today, tomorrow], new Dictionary<string, JobStatus>(), Now, perJobLimit: 5);

        runs.Should().ContainSingle();
        runs[0].Title.Should().Be("今天开会");
        runs[0].When.Should().Be(Now.Date.AddHours(18));
    }

    [Fact]
    public void NextRunAfter_ShouldReturnEarliestFutureRunAcrossJobs()
    {
        var later = new ScheduledJobSpec
        {
            Id = "later",
            Name = "明天起床",
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Once, RunAt = Now.Date.AddDays(1).AddHours(8) }
        };
        var sooner = new ScheduledJobSpec
        {
            Id = "sooner",
            Name = "今晚睡觉",
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Once, RunAt = Now.Date.AddHours(21) }
        };

        var next = HomeDashboardView.NextRunAfter([later, sooner], new Dictionary<string, JobStatus>(), Now);

        next.Should().NotBeNull();
        next!.Title.Should().Be("今晚睡觉");
        next.When.Should().Be(Now.Date.AddHours(21));
    }

    [Fact]
    public void NextRunAfter_ShouldIgnoreExpiredDisabledAndPastJobs()
    {
        var expired = new ScheduledJobSpec
        {
            Id = "past",
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Once, RunAt = Now.AddHours(-2) }
        };
        var disabled = new ScheduledJobSpec
        {
            Id = "off",
            Intent = ScheduleIntent.Disabled,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Once, RunAt = Now.AddHours(1) }
        };

        HomeDashboardView.NextRunAfter([expired, disabled], new Dictionary<string, JobStatus>(), Now)
            .Should().BeNull();
    }

    [Fact]
    public void NextRunAfter_ShouldSkipInvalidScheduleExpression()
    {
        var broken = new ScheduledJobSpec
        {
            Id = "broken",
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "not-an-interval" }
        };

        HomeDashboardView.NextRunAfter([broken], new Dictionary<string, JobStatus>(), Now)
            .Should().BeNull();
    }

    [Fact]
    public void HourLoads_ShouldCountRunsPerHour()
    {
        var runs = new List<TodayRun>
        {
            new(Now.Date.AddHours(20).AddMinutes(53), "测试1", "job-1"),
            new(Now.Date.AddHours(20).AddMinutes(56), "测试2", "job-2"),
            new(Now.Date.AddHours(21), "测试3", "job-3")
        };

        var loads = HomeDashboardView.HourLoads(runs);

        loads.Should().HaveCount(24);
        loads[20].Should().Be(2);
        loads[21].Should().Be(1);
        loads.Where((_, hour) => hour is not (20 or 21)).Should().OnlyContain(c => c == 0);
    }

    [Fact]
    public void HourLoads_EmptyInput_ShouldReturn24Zeros()
    {
        HomeDashboardView.HourLoads(Array.Empty<TodayRun>())
            .Should().HaveCount(24).And.OnlyContain(c => c == 0);
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
    public void TodayExecutions_ShouldKeepOnlyTodayAndCompleted()
    {
        var records = new List<JobExecutionRecord>
        {
            new() { JobId = "a", StartedAt = Now.Date.AddHours(7), CompletedAt = Now.Date.AddHours(7).AddSeconds(3), Status = "success" },
            new() { JobId = "b", StartedAt = Now.Date.AddHours(9), CompletedAt = null, Status = "running" },   // 进行中：不算已执行
            new() { JobId = "c", StartedAt = Now.Date.AddDays(-1).AddHours(22), CompletedAt = Now.Date.AddDays(-1).AddHours(22), Status = "success" }  // 昨天
        };

        var result = HomeDashboardView.TodayExecutions(records, Now);

        result.Should().ContainSingle("只保留今天且已结束的执行");
        result[0].JobId.Should().Be("a");
        result[0].IsSuccess.Should().BeTrue();
        result[0].Duration.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void TodayExecutions_ShouldOrderByLatestFirst_AndRespectLimit()
    {
        var records = new List<JobExecutionRecord>
        {
            new() { JobId = "early", StartedAt = Now.Date.AddHours(1), CompletedAt = Now.Date.AddHours(1), Status = "success" },
            new() { JobId = "late",  StartedAt = Now.Date.AddHours(20), CompletedAt = Now.Date.AddHours(20), Status = "success" },
            new() { JobId = "mid",   StartedAt = Now.Date.AddHours(12), CompletedAt = Now.Date.AddHours(12), Status = "success" }
        };

        var all = HomeDashboardView.TodayExecutions(records, Now);
        all.Select(x => x.JobId).Should().Equal("late", "mid", "early");

        HomeDashboardView.TodayExecutions(records, Now, limit: 2)
            .Select(x => x.JobId).Should().Equal("late", "mid");
    }

    [Fact]
    public void TodayExecutions_FailedStatus_ShouldNotBeSuccess()
    {
        var records = new List<JobExecutionRecord>
        {
            new() { JobId = "f", StartedAt = Now.Date.AddHours(8), CompletedAt = Now.Date.AddHours(8), Status = "failed" }
        };

        var result = HomeDashboardView.TodayExecutions(records, Now);

        result.Should().ContainSingle();
        result[0].IsSuccess.Should().BeFalse("失败记录要能让 UI 标红");
        result[0].Status.Should().Be("failed");
    }

    [Fact]
    public void TodayExecutions_EmptyInput_ShouldReturnEmpty()
    {
        HomeDashboardView.TodayExecutions(Array.Empty<JobExecutionRecord>(), Now).Should().BeEmpty();
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
    public void TodayRuns_TitleShouldFallbackToIdWhenNameMissing()
    {
        var job = new ScheduledJobSpec
        {
            Id = "job_abc",
            Name = null,
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Once, RunAt = Now.AddHours(1) }
        };

        HomeDashboardView.TodayRuns([job], new Dictionary<string, JobStatus>(), Now)
            .Single().Title.Should().Be("job_abc");
    }

    [Fact]
    public void TodayRuns_ShouldCarryJobIdForRowNavigation()
    {
        // 首页待触发行可点击进 /cron-jobs/{JobId} 详情：
        // JobId 若丢失（空字符串），点击会跳到不存在的任务页。
        var job = new ScheduledJobSpec
        {
            Id = "job_detail",
            Name = "巡检",
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Once, RunAt = Now.AddHours(2) }
        };

        var run = HomeDashboardView.TodayRuns([job], new Dictionary<string, JobStatus>(), Now).Single();

        run.JobId.Should().Be("job_detail");
        run.Title.Should().Be("巡检");
    }

    [Fact]
    public void NextRunAfter_ShouldCarryJobIdForRowNavigation()
    {
        // 「下次：…」这行同样可点击，JobId 必须一起返回
        var job = new ScheduledJobSpec
        {
            Id = "job_next",
            Name = "明天的巡检",
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Once, RunAt = Now.AddDays(1).AddHours(9) }
        };

        var next = HomeDashboardView.NextRunAfter([job], new Dictionary<string, JobStatus>(), Now);

        next.Should().NotBeNull();
        next!.JobId.Should().Be("job_next");
    }
}

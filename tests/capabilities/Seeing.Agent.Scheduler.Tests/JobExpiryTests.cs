using FluentAssertions;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Models;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

/// <summary>已过期判定：启用意图但不会再自动触发。</summary>
public class JobExpiryTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 15, 0, 0);

    private static ScheduledJobSpec Job(ScheduleSpec schedule, ScheduleIntent intent = ScheduleIntent.Active) => new()
    {
        Id = "j",
        Intent = intent,
        Schedule = schedule
    };

    [Fact]
    public void Once_WithPastRunAt_ShouldBeExpired()
    {
        var job = Job(new ScheduleSpec { Type = ScheduleTypes.Once, RunAt = Now.AddMinutes(-1) });

        JobExpiry.IsExpired(job, null, Now, schedulerStarted: false).Should().BeTrue();
    }

    [Fact]
    public void Once_WithFutureRunAt_ShouldNotBeExpired()
    {
        var job = Job(new ScheduleSpec { Type = ScheduleTypes.Once, RunAt = Now.AddMinutes(1) });

        JobExpiry.IsExpired(job, null, Now, schedulerStarted: false).Should().BeFalse();
    }

    [Fact]
    public void Once_DisabledIntent_ShouldNotBeExpired()
    {
        var job = Job(
            new ScheduleSpec { Type = ScheduleTypes.Once, RunAt = Now.AddHours(-1) },
            ScheduleIntent.Disabled);

        JobExpiry.IsExpired(job, null, Now, schedulerStarted: true).Should().BeFalse();
    }

    [Fact]
    public void Once_AlreadyFiredWithoutNext_ShouldBeExpired()
    {
        var job = Job(new ScheduleSpec { Type = ScheduleTypes.Once, RunAt = Now.AddHours(1) });
        var status = new JobStatus { JobId = "j", PreviousFireTime = Now.AddMinutes(-5), NextFireTime = null };

        JobExpiry.IsExpired(job, status, Now, schedulerStarted: true).Should().BeTrue();
    }

    [Fact]
    public void Cron_SchedulerNotStarted_ShouldNotBeExpired()
    {
        var job = Job(new ScheduleSpec { Type = ScheduleTypes.Cron, Cron = "0 12 * * *" });

        JobExpiry.IsExpired(job, null, Now, schedulerStarted: false).Should().BeFalse();
    }

    [Fact]
    public void Cron_NoNextFireButFiredBefore_ShouldBeExpired()
    {
        var job = Job(new ScheduleSpec { Type = ScheduleTypes.Cron, Cron = "0 12 31 10 ? 2026" });
        var status = new JobStatus { JobId = "j", PreviousFireTime = Now.AddHours(-1), NextFireTime = null };

        JobExpiry.IsExpired(job, status, Now, schedulerStarted: true).Should().BeTrue();
    }

    [Fact]
    public void Cron_NoNextFireAndNeverFired_ShouldNotBeExpired()
    {
        var job = Job(new ScheduleSpec { Type = ScheduleTypes.Cron, Cron = "0 12 * * *" });
        var status = new JobStatus { JobId = "j", PreviousFireTime = null, NextFireTime = null };

        JobExpiry.IsExpired(job, status, Now, schedulerStarted: true).Should().BeFalse();
    }

    [Fact]
    public void Interval_WithNextFire_ShouldNotBeExpired()
    {
        var job = Job(new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "6h" });
        var status = new JobStatus { JobId = "j", PreviousFireTime = Now.AddHours(-6), NextFireTime = Now.AddHours(1) };

        JobExpiry.IsExpired(job, status, Now, schedulerStarted: true).Should().BeFalse();
    }
}

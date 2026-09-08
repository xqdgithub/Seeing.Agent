using FluentAssertions;
using Seeing.Agent.Scheduler;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Models;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

public class ScheduleWindowsValidatorTests
{
    [Fact]
    public void Empty_Or_Null_Is_Ok()
    {
        ScheduleWindowsValidator.TryNormalize(null, out var n1, out var e1).Should().BeTrue();
        n1.Should().BeEmpty();
        e1.Should().BeNull();
        ScheduleWindowsValidator.TryNormalize(new List<ScheduleWindow>(), out var n2, out _).Should().BeTrue();
        n2.Should().BeEmpty();
    }

    [Fact]
    public void Defaults_Start_And_End()
    {
        ScheduleWindowsValidator.TryNormalize(
            new[] { new ScheduleWindow() },
            out var n, out var err).Should().BeTrue();
        err.Should().BeNull();
        n.Should().ContainSingle();
        n[0].Start.Should().Be(TimeSpan.Zero);
        n[0].End.Should().Be(new TimeSpan(23, 59, 59));
        n[0].CrossesMidnight.Should().BeFalse();
    }

    [Fact]
    public void Touching_Endpoints_Allowed()
    {
        var ok = ScheduleWindowsValidator.TryNormalize(new[]
        {
            new ScheduleWindow { Start = "09:00", End = "12:00" },
            new ScheduleWindow { Start = "12:00", End = "18:00" }
        }, out _, out var err);
        ok.Should().BeTrue();
        err.Should().BeNull();
    }

    [Fact]
    public void Overlap_Rejected()
    {
        var ok = ScheduleWindowsValidator.TryNormalize(new[]
        {
            new ScheduleWindow { Start = "09:00", End = "12:00" },
            new ScheduleWindow { Start = "11:00", End = "14:00" }
        }, out _, out var err);
        ok.Should().BeFalse();
        err.Should().Contain("交叠");
    }

    [Fact]
    public void Overnight_Allowed()
    {
        ScheduleWindowsValidator.TryNormalize(new[]
        {
            new ScheduleWindow { Start = "22:00", End = "06:00" }
        }, out var n, out _).Should().BeTrue();
        n[0].CrossesMidnight.Should().BeTrue();
    }

    [Fact]
    public void Overnight_Overlapping_SameDay_Rejected()
    {
        ScheduleWindowsValidator.TryNormalize(new[]
        {
            new ScheduleWindow { Start = "22:00", End = "06:00" },
            new ScheduleWindow { Start = "05:00", End = "08:00" }
        }, out _, out var err).Should().BeFalse();
        err.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Cron_With_Windows_Rejected()
    {
        ScheduleWindowsValidator.ValidateForScheduleType(
            ScheduleTypes.Cron,
            new[] { new ScheduleWindow { Start = "09:00", End = "18:00" } },
            out var err).Should().BeFalse();
        err.Should().Contain("interval");
    }
}

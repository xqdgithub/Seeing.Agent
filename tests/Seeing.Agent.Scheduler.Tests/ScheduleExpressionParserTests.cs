using FluentAssertions;
using Seeing.Agent.Scheduler;
using Seeing.Agent.Scheduler.Engine;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

public class ScheduleExpressionParserTests
{
    [Theory]
    [InlineData("30m", 30)]
    [InlineData("6h", 360)]
    [InlineData("1h30m", 90)]
    [InlineData("45s", 0.75)]
    public void ParseInterval_ParsesCommonFormats(string input, double expectedMinutes)
    {
        var result = ScheduleExpressionParser.ParseInterval(input);
        result.TotalMinutes.Should().BeApproximately(expectedMinutes, 0.001);
    }

    [Fact]
    public void NormalizeCron_AddsMissingMinuteField()
    {
        ScheduleExpressionParser.NormalizeCron("0 9 * * *").Should().Be("0 9 * * *");
        ScheduleExpressionParser.NormalizeCron("9 * * *").Should().Be("0 9 * * *");
    }

    [Fact]
    public void GetNextOccurrence_Interval_ReturnsFutureTime()
    {
        var from = DateTimeOffset.UtcNow;
        var schedule = new Models.ScheduleSpec
        {
            Type = ScheduleTypes.Interval,
            Every = "1h"
        };

        var next = ScheduleExpressionParser.GetNextOccurrence(schedule, from, "UTC");
        next.Should().NotBeNull();
        next!.Value.Should().BeCloseTo(from.AddHours(1), TimeSpan.FromSeconds(1));
    }
}

public class ActiveHoursCheckerTests
{
    [Fact]
    public void IsInActiveHours_NullOptions_ReturnsTrue()
    {
        Heartbeat.ActiveHoursChecker.IsInActiveHours(null).Should().BeTrue();
    }
}

using FluentAssertions;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Models;
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
    public void NormalizeCron_AddsMissingSecondField()
    {
        // 5 字段 -> 6 字段 + Quartz ? 修正
        ScheduleExpressionParser.NormalizeCron("0 9 * * *").Should().Be("0 0 9 * * ?");
        // 4 字段 -> 6 字段
        ScheduleExpressionParser.NormalizeCron("9 * * *").Should().Be("0 0 9 * * ?");
    }

    [Fact]
    public void GetNextOccurrence_Interval_ReturnsFutureTime()
    {
        var from = DateTime.UtcNow;
        var schedule = new ScheduleSpec
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
        ActiveHoursChecker.IsInActiveHours(null).Should().BeTrue();
    }
}

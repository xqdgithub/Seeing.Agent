using FluentAssertions;
using Seeing.Agent.Scheduler;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Models;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

public class ScheduleExpressionParserWindowsTests
{
    [Fact]
    public void Interval_Windows_Aligns_To_Window_Start()
    {
        var schedule = new ScheduleSpec
        {
            Type = ScheduleTypes.Interval,
            Every = "40m",
            Timezone = "Asia/Shanghai",
            Windows = new List<ScheduleWindow>
            {
                new() { Start = "09:00", End = "23:00" }
            }
        };

        var fromLocal = new DateTime(2026, 7, 20, 10, 5, 0, DateTimeKind.Unspecified);
        var next = ScheduleExpressionParser.GetNextOccurrence(schedule, fromLocal);
        next.Should().NotBeNull();
        next!.Value.Hour.Should().Be(10);
        next.Value.Minute.Should().Be(20);
    }

    [Fact]
    public void Interval_Empty_Windows_Unchanged()
    {
        var schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "30m" };
        var from = new DateTime(2026, 7, 20, 10, 0, 0);
        ScheduleExpressionParser.GetNextOccurrence(schedule, from)
            .Should().Be(from.AddMinutes(30));
    }
}

using FluentAssertions;
using Seeing.Agent.Abstractions.Reminders;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Agent.Scheduler.Models;
using Seeing.Serialization;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

public class ScheduleRunProfileTests
{
    [Fact]
    public void Cron_Defaults_MatchTodaysCronBehaviour()
    {
        var profile = ScheduleRunProfile.Cron;

        profile.Source.Should().Be(ScheduleSources.Cron);
        profile.ReminderKind.Should().Be(SystemReminder.Kinds.Cron);
        profile.SenderId.Should().Be("cron");
        profile.SessionKeyPrefix.Should().Be("cron");
        profile.OriginWhenCreated.Should().Be(SessionOrigin.Scheduler);
        profile.Layout.Should().Be(DispatchLayout.PromptAndResult);
    }

    [Fact]
    public void Heartbeat_Profile_UsesHeartbeatIdentityAndAssistantOnlyLayout()
    {
        var profile = ScheduleRunProfile.Heartbeat;

        profile.Source.Should().Be(ScheduleSources.Heartbeat);
        profile.ReminderKind.Should().Be(SystemReminder.Kinds.Heartbeat);
        profile.SenderId.Should().Be("heartbeat");
        profile.SessionKeyPrefix.Should().Be("hb");
        profile.OriginWhenCreated.Should().Be(SessionOrigin.Heartbeat);
        profile.Layout.Should().Be(DispatchLayout.AssistantOnly);
    }

    [Fact]
    public void Profile_RoundTrips_ThroughJson()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            ScheduleRunProfile.Heartbeat, SeeingJson.Readable);
        var back = System.Text.Json.JsonSerializer.Deserialize<ScheduleRunProfile>(json);

        back.Should().NotBeNull();
        back!.Source.Should().Be(ScheduleSources.Heartbeat);
        back.Layout.Should().Be(DispatchLayout.AssistantOnly);
        back.OriginWhenCreated.Should().Be(SessionOrigin.Heartbeat);
    }

    [Fact]
    public void DispatchRequest_Defaults_StayOnCron()
    {
        var request = new DispatchRequest
        {
            Source = "cron",
            TaskType = "agent",
            Content = "x"
        };

        request.ReminderKind.Should().Be(SystemReminder.Kinds.Cron);
        request.SenderId.Should().Be("cron");
        request.Layout.Should().Be(DispatchLayout.PromptAndResult);
    }
}

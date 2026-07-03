using FluentAssertions;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Scheduler.Tests.Fixtures;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

public class SchedulerOptionsProviderTests
{
    [Fact]
    public void Reload_LoadsSchedulerSectionFromProjectSeeingJson()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson(new SchedulerOptions
        {
            Enabled = true,
            Timezone = "Asia/Shanghai",
            Heartbeat = new HeartbeatOptions
            {
                Enabled = true,
                Every = "30m",
                Target = HeartbeatTargets.Last,
                QueryFile = "HEARTBEAT.md"
            }
        });

        var provider = ws.CreateOptionsProvider();

        provider.Current.Enabled.Should().BeTrue();
        provider.Current.Timezone.Should().Be("Asia/Shanghai");
        provider.Current.Heartbeat.Enabled.Should().BeTrue();
        provider.Current.Heartbeat.Every.Should().Be("30m");
        provider.Current.Heartbeat.Target.Should().Be(HeartbeatTargets.Last);
    }

    [Fact]
    public void Reload_ProjectOverridesDefaults()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson(new SchedulerOptions { Enabled = false });

        var provider = ws.CreateOptionsProvider();
        provider.Current.Enabled.Should().BeFalse();
    }
}

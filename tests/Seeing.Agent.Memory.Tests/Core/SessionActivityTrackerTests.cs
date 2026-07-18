using FluentAssertions;
using Seeing.Agent.Memory.Core;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Core;

public class SessionActivityTrackerTests
{
    [Fact]
    public void GetIdleSessions_WhenTouchedRecently_ShouldBeEmpty()
    {
        var tracker = new SessionActivityTracker();
        tracker.Touch("s1");
        tracker.GetIdleSessions(TimeSpan.FromMinutes(15)).Should().BeEmpty();
    }

    [Fact]
    public void Clear_ShouldRemoveSession()
    {
        var tracker = new SessionActivityTracker();
        tracker.Touch("s1");
        tracker.Clear("s1");
        // Force idle by using zero threshold after clear — cleared sessions absent
        tracker.GetIdleSessions(TimeSpan.Zero).Should().NotContain("s1");
    }
}

using FluentAssertions;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class ActiveSessionTrackerTests
{
    [Fact]
    public void Attach_SameSessionFromMultipleCircuits_ShouldAttachPresenceOnce()
    {
        var presence = new PermissionPresenceStore();
        var tracker = new ActiveSessionTracker(presence);

        tracker.Attach("s1", "c1");
        tracker.Attach("s1", "c2");

        tracker.IsActive("s1").Should().BeTrue();
        presence.CanPresent("s1").Should().BeTrue();

        tracker.Detach("s1", "c1");
        presence.CanPresent("s1").Should().BeTrue();

        tracker.Detach("s1", "c2");
        presence.CanPresent("s1").Should().BeFalse();
        tracker.IsActive("s1").Should().BeFalse();
    }

    [Fact]
    public void Attach_SameSessionSameCircuit_ShouldBeIdempotent()
    {
        var presence = new PermissionPresenceStore();
        var tracker = new ActiveSessionTracker(presence);

        tracker.Attach("s1", "c1");
        tracker.Attach("s1", "c1");
        tracker.Detach("s1", "c1");

        tracker.IsActive("s1").Should().BeFalse();
        presence.CanPresent("s1").Should().BeFalse();
    }

    [Fact]
    public void Attach_DifferentSessionInSameCircuit_ShouldDetachPrevious()
    {
        var presence = new PermissionPresenceStore();
        var tracker = new ActiveSessionTracker(presence);

        tracker.Attach("s1", "c1");
        tracker.Attach("s2", "c1");

        tracker.IsActive("s1").Should().BeFalse();
        tracker.IsActive("s2").Should().BeTrue();
        presence.CanPresent("s1").Should().BeFalse();
        presence.CanPresent("s2").Should().BeTrue();
    }

    [Fact]
    public void DetachCircuit_ShouldDetachTrackedSession()
    {
        var presence = new PermissionPresenceStore();
        var tracker = new ActiveSessionTracker(presence);

        tracker.Attach("s1", "c1");
        tracker.DetachCircuit("c1");

        tracker.IsActive("s1").Should().BeFalse();
        presence.CanPresent("s1").Should().BeFalse();
    }

    [Fact]
    public void DetachCircuit_OtherCircuitStillPresent_ShouldKeepSessionActive()
    {
        var presence = new PermissionPresenceStore();
        var tracker = new ActiveSessionTracker(presence);

        tracker.Attach("s1", "c1");
        tracker.Attach("s1", "c2");
        tracker.DetachCircuit("c1");

        tracker.IsActive("s1").Should().BeTrue();
        presence.CanPresent("s1").Should().BeTrue();

        tracker.DetachCircuit("c2");
        presence.CanPresent("s1").Should().BeFalse();
    }

    [Fact]
    public void Detach_WrongCircuit_ShouldNotDetach()
    {
        var presence = new PermissionPresenceStore();
        var tracker = new ActiveSessionTracker(presence);

        tracker.Attach("s1", "c1");
        tracker.Detach("s1", "c2");

        tracker.IsActive("s1").Should().BeTrue();
        presence.CanPresent("s1").Should().BeTrue();
    }

    [Fact]
    public void Attach_EmptySessionId_ShouldBeIgnored()
    {
        var presence = new PermissionPresenceStore();
        var tracker = new ActiveSessionTracker(presence);

        tracker.Attach("", "c1");

        tracker.IsActive("").Should().BeFalse();
        presence.CanPresent("").Should().BeFalse();
    }
}

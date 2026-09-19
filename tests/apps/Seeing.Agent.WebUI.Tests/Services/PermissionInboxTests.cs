using FluentAssertions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class PermissionInboxTests
{
    private const string Session = "s1";

    private static PermissionRequest Pending(
        string requestId,
        string? callId = "call-1",
        string sessionId = Session,
        IReadOnlyList<PermissionGrantScope>? scopes = null)
        => new()
        {
            RequestId = requestId,
            SessionId = sessionId,
            CallId = callId,
            PermissionKind = "tool.execute",
            Message = "需要确认",
            AllowedScopes = scopes ?? Array.Empty<PermissionGrantScope>()
        };

    [Fact]
    public void GetAll_ShouldProjectPendingRequests()
    {
        var manager = new FakePermissionRequestManager();
        manager.Seed(Pending("r1", "call-1"), Pending("r2", "call-2"));

        var inbox = new PermissionInbox(manager);

        inbox.GetAll().Select(c => c.RequestId).Should().Equal("r1", "r2");
        inbox.TotalCount.Should().Be(2);
        inbox.GetAll().Should().OnlyContain(c => c.IsPending);
    }

    [Fact]
    public void GetAll_EmptyAllowedScopes_ShouldDefaultToOnceAndSession()
    {
        var manager = new FakePermissionRequestManager();
        manager.Seed(Pending("r1"));
        var inbox = new PermissionInbox(manager);

        var card = inbox.GetAll().Single();

        card.Allows(PermissionGrantScope.Once).Should().BeTrue();
        card.Allows(PermissionGrantScope.Session).Should().BeTrue();
        card.CanAllowSessionDirectory.Should().BeFalse();
    }

    [Fact]
    public void GetBySession_And_CountBySession_ShouldFilterBySession()
    {
        var manager = new FakePermissionRequestManager();
        manager.Seed(Pending("r1", sessionId: "s1"), Pending("r2", sessionId: "s2"), Pending("r3", sessionId: "s1"));
        var inbox = new PermissionInbox(manager);

        inbox.GetBySession("s1").Select(c => c.RequestId).Should().BeEquivalentTo(new[] { "r1", "r3" });
        inbox.CountBySession("s1").Should().Be(2);
        inbox.CountBySession("nope").Should().Be(0);
        inbox.CountBySession(null).Should().Be(0);
    }

    [Fact]
    public void GetByCallId_ShouldFilterByCallId()
    {
        var manager = new FakePermissionRequestManager();
        manager.Seed(Pending("r1", "call-x"), Pending("r2", "call-y"));
        var inbox = new PermissionInbox(manager);

        inbox.GetByCallId("call-x").Should().ContainSingle(c => c.RequestId == "r1");
        inbox.GetByCallId(null).Should().BeEmpty();
    }

    [Fact]
    public void PendingChanged_ShouldRebuildProjection()
    {
        var manager = new FakePermissionRequestManager();
        var inbox = new PermissionInbox(manager);
        inbox.TotalCount.Should().Be(0);

        manager.Seed(Pending("r1"));

        inbox.TotalCount.Should().Be(1);
        inbox.GetAll().Single().RequestId.Should().Be("r1");
    }

    [Fact]
    public void PendingChanged_ShouldFireChanged()
    {
        var manager = new FakePermissionRequestManager();
        var inbox = new PermissionInbox(manager);
        var fired = 0;
        inbox.Changed += () => fired++;

        manager.Seed(Pending("r1"));
        manager.Remove("r1");

        fired.Should().Be(2);
        inbox.TotalCount.Should().Be(0);
    }
}

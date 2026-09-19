using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class PermissionInboxViewTests
{
    private const string Anchor = "anchor";
    private const string Child = "child";
    private const string Outsider = "outsider";

    private static PermissionRequest Pending(string requestId, string sessionId, string? callId = null)
        => new()
        {
            RequestId = requestId,
            SessionId = sessionId,
            CallId = callId,
            PermissionKind = "tool.execute"
        };

    private static SessionGroup AnchorAndChildGroup() => new()
    {
        Id = "g1",
        AnchorSessionId = Anchor,
        ActiveSessionId = Anchor,
        Version = 1,
        Members = new List<SessionGroupMember>
        {
            new() { SessionId = Anchor, Relation = SessionRelation.None, IsAnchor = true, Order = 0 },
            new() { SessionId = Child, Relation = SessionRelation.Child, ParentSessionId = Anchor, Order = 1 }
        }
    };

    private static SessionWindowRegistry CreateRegistry(SessionGroup group)
    {
        var gm = new Mock<ISessionGroupManager>();
        gm.Setup(m => m.GetGroupForSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(group);
        gm.Setup(m => m.GetGroupAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(group);
        return new SessionWindowRegistry(
            gm.Object, new ChannelSessionGroupEventBus(), Mock.Of<ISessionManager>(),
            NullLogger<SessionWindowRegistry>.Instance);
    }

    private static async Task<SessionWindowRegistry> ReadyRegistryAsync()
    {
        var registry = CreateRegistry(AnchorAndChildGroup());
        registry.Rebind(Anchor);
        await Task.Delay(200);
        return registry;
    }

    [Fact]
    public async Task CardsForScope_ShouldIncludeOnlyRegistryWindows()
    {
        var manager = new FakePermissionRequestManager();
        manager.Seed(
            Pending("r-anchor", Anchor, "call-1"),
            Pending("r-child", Child, "call-2"),
            Pending("r-out", Outsider, "call-3"));
        var inbox = new PermissionInbox(manager);
        var registry = await ReadyRegistryAsync();

        using var view = new PermissionInboxView(
            inbox, () => registry, TimeSpan.Zero, NullLogger<PermissionInboxView>.Instance);

        view.CardsForScope.Select(c => c.RequestId)
            .Should().BeEquivalentTo(new[] { "r-anchor", "r-child" });
        view.TotalCount.Should().Be(2);
        view.CountBySession(Child).Should().Be(1);
        view.GetBySession(Child).Should().ContainSingle(c => c.RequestId == "r-child");
    }

    [Fact]
    public async Task GetByCallId_ShouldReturnScopedCard()
    {
        var manager = new FakePermissionRequestManager();
        manager.Seed(Pending("r-child", Child, "call-2"), Pending("r-out", Outsider, "call-3"));
        var inbox = new PermissionInbox(manager);
        var registry = await ReadyRegistryAsync();

        using var view = new PermissionInboxView(
            inbox, () => registry, TimeSpan.Zero, NullLogger<PermissionInboxView>.Instance);

        view.GetByCallId("call-2").Should().ContainSingle(c => c.RequestId == "r-child");
        view.GetByCallId("call-3").Should().BeEmpty();
    }

    [Fact]
    public void Prerender_NullRegistry_ShouldBeEmptyAndNeverNotify()
    {
        var manager = new FakePermissionRequestManager();
        var inbox = new PermissionInbox(manager);
        using var view = new PermissionInboxView(
            inbox, () => null, TimeSpan.Zero, NullLogger<PermissionInboxView>.Instance);
        var fired = 0;
        view.Changed += () => fired++;

        manager.Seed(Pending("r1", Anchor, "call-1"));
        Thread.Sleep(80);

        view.TotalCount.Should().Be(0);
        fired.Should().Be(0);
    }

    [Fact]
    public async Task Dispose_ShouldUnsubscribe()
    {
        var manager = new FakePermissionRequestManager();
        manager.Seed(Pending("r1", Anchor, "call-1"));
        var inbox = new PermissionInbox(manager);
        var registry = await ReadyRegistryAsync();
        var view = new PermissionInboxView(
            inbox, () => registry, TimeSpan.Zero, NullLogger<PermissionInboxView>.Instance);
        var fired = 0;
        view.Changed += () => fired++;

        view.Dispose();
        manager.Seed(Pending("r2", Anchor, "call-2"));
        await Task.Delay(100);

        fired.Should().Be(0);
        view.TotalCount.Should().Be(0);
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Tests.Services;

public class SessionWindowRegistryTests
{
    private static SessionGroupMember Member(
        string sessionId,
        SessionRelation relation,
        string? parent = null,
        bool anchor = false,
        int order = 0,
        string? label = null) => new()
        {
            SessionId = sessionId,
            Relation = relation,
            ParentSessionId = parent,
            IsAnchor = anchor,
            Order = order,
            Label = label
        };

    private static SessionGroup CreateGroup(
        string groupId,
        string anchorId,
        string? activeId,
        long version,
        params SessionGroupMember[] members) => new()
        {
            Id = groupId,
            AnchorSessionId = anchorId,
            ActiveSessionId = activeId,
            Version = version,
            Members = members.ToList()
        };

    private static Mock<ISessionGroupManager> CreateGroupManager(SessionGroup group)
    {
        var gm = new Mock<ISessionGroupManager>();
        gm.Setup(m => m.GetGroupForSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(group);
        gm.Setup(m => m.GetGroupAsync(group.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(group);
        return gm;
    }

    private static SessionWindowRegistry CreateRegistry(
        Mock<ISessionGroupManager> groupManager,
        ISessionGroupEventBus bus)
        => new(groupManager.Object, bus, Mock.Of<ISessionManager>(),
            NullLogger<SessionWindowRegistry>.Instance);

    [Fact]
    public async Task Rebind_ShouldLoadMembersWithRelation()
    {
        var group = CreateGroup("g1", "anchor", "anchor", 3,
            Member("anchor", SessionRelation.None, anchor: true, order: 0),
            Member("child1", SessionRelation.Child, parent: "anchor", order: 1));
        var gm = CreateGroupManager(group);
        var bus = new ChannelSessionGroupEventBus();

        using var registry = CreateRegistry(gm, bus);
        registry.Rebind("anchor");
        await Task.Delay(200, TestContext.Current.CancellationToken);

        registry.GroupId.Should().Be("g1");
        registry.AnchorSessionId.Should().Be("anchor");
        registry.ActiveSessionId.Should().Be("anchor");
        registry.Windows.Should().HaveCount(2);
        registry.Windows.Should().Contain(w =>
            w.SessionId == "child1"
            && w.Relation == SessionRelation.Child
            && w.Kind == SessionKind.SubAgent
            && !w.IsActive);
        registry.Windows.Should().Contain(w =>
            w.SessionId == "anchor"
            && w.Relation == SessionRelation.None
            && w.Kind == SessionKind.Root
            && w.IsActive);
    }

    [Fact]
    public async Task GroupEvent_ShouldUpdateActiveAndMembers()
    {
        var group = CreateGroup("g1", "anchor", "anchor", 3,
            Member("anchor", SessionRelation.None, anchor: true, order: 0),
            Member("child1", SessionRelation.Child, parent: "anchor", order: 1));
        var gm = CreateGroupManager(group);
        var bus = new ChannelSessionGroupEventBus();

        using var registry = CreateRegistry(gm, bus);
        var changed = 0;
        registry.WindowsChanged += () => changed++;
        registry.Rebind("anchor");
        await Task.Delay(200, TestContext.Current.CancellationToken);

        bus.Publish(new SessionGroupChangedEvent
        {
            GroupId = "g1",
            AnchorSessionId = "anchor",
            ActiveSessionId = "child1",
            Version = 4,
            Members = new List<SessionGroupMember>
            {
                Member("anchor", SessionRelation.None, anchor: true, order: 0),
                Member("child1", SessionRelation.Child, parent: "anchor", order: 1)
            }
        });
        await Task.Delay(200, TestContext.Current.CancellationToken);

        registry.ActiveSessionId.Should().Be("child1");
        registry.Windows.Should().Contain(w => w.SessionId == "child1" && w.IsActive);
        registry.Windows.Should().Contain(w => w.SessionId == "anchor" && !w.IsActive);
        changed.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GroupEvent_StaleVersion_ShouldBeIgnored()
    {
        var group = CreateGroup("g1", "anchor", "anchor", 5,
            Member("anchor", SessionRelation.None, anchor: true, order: 0),
            Member("child1", SessionRelation.Child, parent: "anchor", order: 1));
        var gm = CreateGroupManager(group);
        var bus = new ChannelSessionGroupEventBus();

        using var registry = CreateRegistry(gm, bus);
        registry.Rebind("anchor");
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // 乱序到达的旧快照（Version=2 < 已应用 5）：不得覆盖 active / 成员集合
        bus.Publish(new SessionGroupChangedEvent
        {
            GroupId = "g1",
            AnchorSessionId = "anchor",
            ActiveSessionId = "child1",
            Version = 2,
            Members = new List<SessionGroupMember>
            {
                Member("child1", SessionRelation.Child, parent: "anchor", order: 0)
            }
        });
        await Task.Delay(200, TestContext.Current.CancellationToken);

        registry.ActiveSessionId.Should().Be("anchor");
        registry.Windows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Rebind_ShouldProjectAnchorAndLabel()
    {
        var group = CreateGroup("g1", "anchor", "anchor", 3,
            Member("anchor", SessionRelation.None, anchor: true, order: 0),
            Member("fork1", SessionRelation.Fork, parent: "anchor", order: 1,
                label: "trim-backup 2026-09-18"));
        var gm = CreateGroupManager(group);
        var bus = new ChannelSessionGroupEventBus();

        using var registry = CreateRegistry(gm, bus);
        registry.Rebind("anchor");
        await Task.Delay(200, TestContext.Current.CancellationToken);

        registry.Windows.Should().HaveCount(2);
        registry.Windows.Should().Contain(w =>
            w.SessionId == "anchor"
            && w.IsAnchor
            && w.Label == null);
        registry.Windows.Should().Contain(w =>
            w.SessionId == "fork1"
            && !w.IsAnchor
            && w.Label == "trim-backup 2026-09-18");
    }

    [Fact]
    public async Task Rebind_WithoutGroup_ShouldProjectFallbackAsAnchor()
    {
        var gm = new Mock<ISessionGroupManager>();
        gm.Setup(m => m.GetGroupForSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionGroup?)null);
        var bus = new ChannelSessionGroupEventBus();

        using var registry = CreateRegistry(gm, bus);
        registry.Rebind("solo");
        await Task.Delay(200, TestContext.Current.CancellationToken);

        registry.Windows.Should().ContainSingle(w =>
            w.SessionId == "solo"
            && w.IsAnchor
            && w.Label == null
            && w.Relation == SessionRelation.None);
    }

    [Fact]
    public async Task Dispose_ShouldCancelSubscription()
    {
        var group = CreateGroup("g1", "anchor", "anchor", 3,
            Member("anchor", SessionRelation.None, anchor: true, order: 0),
            Member("child1", SessionRelation.Child, parent: "anchor", order: 1));
        var gm = CreateGroupManager(group);
        var bus = new ChannelSessionGroupEventBus();

        var registry = CreateRegistry(gm, bus);
        registry.Rebind("anchor");
        await Task.Delay(200, TestContext.Current.CancellationToken);

        registry.Dispose();
        await Task.Delay(100, TestContext.Current.CancellationToken); // 等待读循环退出并注销订阅

        bus.Publish(new SessionGroupChangedEvent
        {
            GroupId = "g1",
            AnchorSessionId = "anchor",
            ActiveSessionId = "child1",
            Version = 99,
            Members = new List<SessionGroupMember>
            {
                Member("anchor", SessionRelation.None, anchor: true, order: 0),
                Member("child1", SessionRelation.Child, parent: "anchor", order: 1)
            }
        });
        await Task.Delay(150, TestContext.Current.CancellationToken);

        registry.ActiveSessionId.Should().Be("anchor");
    }
}

using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Session.Tests.Management;

public class SessionGroupManagerTests
{
    [Fact]
    public async Task EnsureForSessionAsync_Concurrent_ShouldCreateSingleGroup()
    {
        using var h = new SessionGroupTestHarness();
        var session = h.CreateRoot();

        var groups = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => h.Manager.EnsureForSessionAsync(session.Id)));

        groups.Select(g => g.Id).Distinct().Should().ContainSingle();
        Directory.GetFiles(h.Dir, "*.json").Should().ContainSingle();

        var persisted = await h.GroupStore.FindBySessionAsync(session.Id, TestContext.Current.CancellationToken);
        persisted.Should().NotBeNull();
        persisted!.Members.Should().ContainSingle();
        persisted.Members[0].IsAnchor.Should().BeTrue();
        persisted.Members[0].Relation.Should().Be(SessionRelation.None);
        persisted.Members[0].Order.Should().Be(0);
        persisted.Version.Should().Be(1);

        h.Sessions.Get(session.Id)!.GroupId.Should().Be(persisted.Id);
    }

    [Fact]
    public async Task EnsureForSessionAsync_ShouldBeIdempotent()
    {
        using var h = new SessionGroupTestHarness();
        var session = h.CreateRoot();

        var first = await h.Manager.EnsureForSessionAsync(session.Id, TestContext.Current.CancellationToken);
        var second = await h.Manager.EnsureForSessionAsync(session.Id, TestContext.Current.CancellationToken);

        second.Id.Should().Be(first.Id);
    }

    [Fact]
    public async Task GetGroupForSessionAsync_ColdFallback_ShouldLoadFromStore()
    {
        using var h = new SessionGroupTestHarness();
        var session = h.CreateRoot();
        await h.Manager.EnsureForSessionAsync(session.Id, TestContext.Current.CancellationToken);

        var cold = h.NewColdManager();
        var group = await cold.GetGroupForSessionAsync(session.Id, TestContext.Current.CancellationToken);

        group.Should().NotBeNull();
        group!.AnchorSessionId.Should().Be(session.Id);
    }

    [Fact]
    public async Task AddMemberAsync_ShouldAssignIncrementalOrderAndIncrementVersion()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot();
        var group = await h.Manager.EnsureForSessionAsync(root.Id, TestContext.Current.CancellationToken);
        var a = h.CreatePlainSession();
        var b = h.CreatePlainSession();

        await h.Manager.AddMemberAsync(group.Id, new SessionGroupMember
        {
            SessionId = a.Id, Relation = SessionRelation.Fork, ParentSessionId = root.Id
        }, TestContext.Current.CancellationToken);
        await h.Manager.AddMemberAsync(group.Id, new SessionGroupMember
        {
            SessionId = b.Id, Relation = SessionRelation.Fork, ParentSessionId = root.Id
        }, TestContext.Current.CancellationToken);

        var reloaded = await h.Manager.GetGroupAsync(group.Id, TestContext.Current.CancellationToken);
        reloaded!.Version.Should().Be(3);
        reloaded.Members.Single(m => m.SessionId == a.Id).Order.Should().Be(1);
        reloaded.Members.Single(m => m.SessionId == b.Id).Order.Should().Be(2);
    }

    [Fact]
    public async Task SetActiveAsync_ShouldUpdateActiveIncrementVersionAndRaiseSnapshot()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot();
        var group = await h.Manager.EnsureForSessionAsync(root.Id, TestContext.Current.CancellationToken);
        var leaf = h.CreatePlainSession();
        await h.Manager.AddMemberAsync(group.Id, new SessionGroupMember
        {
            SessionId = leaf.Id, Relation = SessionRelation.Fork, ParentSessionId = root.Id
        }, TestContext.Current.CancellationToken);

        var versionBefore = (await h.Manager.GetGroupAsync(group.Id, TestContext.Current.CancellationToken))!.Version;

        var snapshots = new List<SessionGroup>();
        h.Manager.Changed += (_, e) => snapshots.Add(e.Group);

        await h.Manager.SetActiveAsync(group.Id, leaf.Id, TestContext.Current.CancellationToken);

        snapshots.Should().ContainSingle();
        snapshots[0].ActiveSessionId.Should().Be(leaf.Id);
        snapshots[0].Version.Should().Be(versionBefore + 1);

        // 快照独立：后续变更不得回写已捕获快照
        await h.Manager.SetActiveAsync(group.Id, root.Id, TestContext.Current.CancellationToken);
        snapshots[0].ActiveSessionId.Should().Be(leaf.Id);
        snapshots.Should().HaveCount(2);
        snapshots[1].ActiveSessionId.Should().Be(root.Id);
        (await h.Manager.GetGroupAsync(group.Id, TestContext.Current.CancellationToken))!.ActiveSessionId.Should().Be(root.Id);
    }

    [Fact]
    public async Task RemoveMemberAsync_WhenActiveRemoved_ShouldFallBackToAnchor()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot();
        var group = await h.Manager.EnsureForSessionAsync(root.Id, TestContext.Current.CancellationToken);
        var leaf = h.CreatePlainSession();
        await h.Manager.AddMemberAsync(group.Id, new SessionGroupMember
        {
            SessionId = leaf.Id, Relation = SessionRelation.Fork, ParentSessionId = root.Id
        }, TestContext.Current.CancellationToken);
        await h.Manager.SetActiveAsync(group.Id, leaf.Id, TestContext.Current.CancellationToken);

        await h.Manager.RemoveMemberAsync(group.Id, leaf.Id, TestContext.Current.CancellationToken);

        var reloaded = await h.Manager.GetGroupAsync(group.Id, TestContext.Current.CancellationToken);
        reloaded!.ActiveSessionId.Should().BeNull();
        reloaded.ResolveActiveId().Should().Be(root.Id);
    }

    [Fact]
    public async Task RemoveMemberAsync_WhenAnchorRemoved_ShouldPromoteLowestOrder()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot();
        var group = await h.Manager.EnsureForSessionAsync(root.Id, TestContext.Current.CancellationToken);
        var first = h.CreatePlainSession();
        var second = h.CreatePlainSession();
        await h.Manager.AddMemberAsync(group.Id, new SessionGroupMember
        {
            SessionId = first.Id, Relation = SessionRelation.Fork, ParentSessionId = root.Id
        }, TestContext.Current.CancellationToken);
        await h.Manager.AddMemberAsync(group.Id, new SessionGroupMember
        {
            SessionId = second.Id, Relation = SessionRelation.Fork, ParentSessionId = root.Id
        }, TestContext.Current.CancellationToken);

        await h.Manager.RemoveMemberAsync(group.Id, root.Id, TestContext.Current.CancellationToken);

        var reloaded = await h.Manager.GetGroupAsync(group.Id, TestContext.Current.CancellationToken);
        reloaded!.Members.Should().NotContain(m => m.SessionId == root.Id);
        var anchor = reloaded.Members.Single(m => m.IsAnchor);
        anchor.SessionId.Should().Be(first.Id);
        anchor.Relation.Should().Be(SessionRelation.None);
        reloaded.AnchorSessionId.Should().Be(first.Id);
    }

    [Fact]
    public async Task RemoveMemberAsync_WhenLastMemberRemoved_ShouldDeleteGroupAndBroadcastEmpty()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot();
        var group = await h.Manager.EnsureForSessionAsync(root.Id, TestContext.Current.CancellationToken);

        SessionGroup? lastSnapshot = null;
        h.Manager.Changed += (_, e) => lastSnapshot = e.Group;

        await h.Manager.RemoveMemberAsync(group.Id, root.Id, TestContext.Current.CancellationToken);

        (await h.Manager.GetGroupAsync(group.Id, TestContext.Current.CancellationToken)).Should().BeNull();
        Directory.GetFiles(h.Dir, "*.json").Should().BeEmpty();
        lastSnapshot.Should().NotBeNull();
        lastSnapshot!.Members.Should().BeEmpty();
    }

    [Fact]
    public async Task Changed_ShouldBeRaisedOutsideLocks()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot();
        var group = await h.Manager.EnsureForSessionAsync(root.Id, TestContext.Current.CancellationToken);
        var leaf = h.CreatePlainSession();
        await h.Manager.AddMemberAsync(group.Id, new SessionGroupMember
        {
            SessionId = leaf.Id, Relation = SessionRelation.Fork, ParentSessionId = root.Id
        }, TestContext.Current.CancellationToken);

        var reentrantCallSucceeded = false;
        var reentered = 0;
        h.Manager.Changed += (_, e) =>
        {
            // 只触发一次嵌套变更，避免无限递归
            if (Interlocked.Exchange(ref reentered, 1) == 1)
                return;

            // 若事件在组锁内触发，此嵌套变更会因无法获取同一锁而死锁。
            var task = Task.Run(() => h.Manager.SetActiveAsync(e.Group.Id, e.Group.Members[1].SessionId));
            reentrantCallSucceeded = task.Wait(TimeSpan.FromSeconds(10));
        };

        await h.Manager.SetActiveAsync(group.Id, root.Id, TestContext.Current.CancellationToken);

        reentrantCallSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveSessionAsync_ShouldCascadeChildSubtree()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot();
        var child = await h.Manager.CreateChildAsync(
            root.Id, "task", "子任务", Array.Empty<SessionPermissionRule>(), null, TestContext.Current.CancellationToken);
        var grandChild = await h.Manager.CreateChildAsync(
            child.Id, "task", "孙任务", Array.Empty<SessionPermissionRule>(), null, TestContext.Current.CancellationToken);
        var fork = h.CreatePlainSession();
        var group = await h.Manager.GetGroupForSessionAsync(root.Id, TestContext.Current.CancellationToken);
        await h.Manager.AddMemberAsync(group!.Id, new SessionGroupMember
        {
            SessionId = fork.Id, Relation = SessionRelation.Fork, ParentSessionId = root.Id
        }, TestContext.Current.CancellationToken);

        await h.Manager.RemoveSessionAsync(root.Id, TestContext.Current.CancellationToken);

        h.Sessions.Get(root.Id).Should().BeNull();
        h.Sessions.Get(child.Id).Should().BeNull();
        h.Sessions.Get(grandChild.Id).Should().BeNull();
        h.Sessions.Get(fork.Id).Should().NotBeNull();

        var reloaded = await h.Manager.GetGroupAsync(group.Id, TestContext.Current.CancellationToken);
        reloaded.Should().NotBeNull();
        reloaded!.Members.Should().ContainSingle(m => m.SessionId == fork.Id);
        reloaded.Members.Single().IsAnchor.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveSessionAsync_WhenGroupBecomesEmpty_ShouldDeleteGroup()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot();
        var child = await h.Manager.CreateChildAsync(
            root.Id, "task", "子任务", Array.Empty<SessionPermissionRule>(), null, TestContext.Current.CancellationToken);
        var group = await h.Manager.GetGroupForSessionAsync(root.Id, TestContext.Current.CancellationToken);

        await h.Manager.RemoveSessionAsync(root.Id, TestContext.Current.CancellationToken);

        h.Sessions.Get(root.Id).Should().BeNull();
        h.Sessions.Get(child.Id).Should().BeNull();
        (await h.Manager.GetGroupAsync(group!.Id, TestContext.Current.CancellationToken)).Should().BeNull();
        Directory.GetFiles(h.Dir, "*.json").Should().BeEmpty();
    }

    [Fact]
    public async Task TryGetParent_ShouldReturnIndexedParent_AndReindexOnRemoval()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot();
        var child = await h.Manager.CreateChildAsync(
            root.Id, "task", "子任务", Array.Empty<SessionPermissionRule>(), null, TestContext.Current.CancellationToken);
        var grandChild = await h.Manager.CreateChildAsync(
            child.Id, "task", "孙任务", Array.Empty<SessionPermissionRule>(), null, TestContext.Current.CancellationToken);

        h.Manager.TryGetParent(child.Id, out var childParent).Should().BeTrue();
        childParent.Should().Be(root.Id);
        h.Manager.TryGetParent(grandChild.Id, out var grandParent).Should().BeTrue();
        grandParent.Should().Be(child.Id);
        h.Manager.TryGetParent(root.Id, out _).Should().BeFalse();

        // 移除中间节点：孙任务重挂到 root，被移除节点不再有父索引
        var group = await h.Manager.GetGroupForSessionAsync(root.Id, TestContext.Current.CancellationToken);
        await h.Manager.RemoveMemberAsync(group!.Id, child.Id, TestContext.Current.CancellationToken);

        h.Manager.TryGetParent(grandChild.Id, out var reparented).Should().BeTrue();
        reparented.Should().Be(root.Id);
        h.Manager.TryGetParent(child.Id, out _).Should().BeFalse();
    }

    [Fact]
    public async Task ListChildrenAsync_ShouldMatchOnlyChildRelationAndSupportColdFallback()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot();
        var child = await h.Manager.CreateChildAsync(
            root.Id, "task", "子任务", Array.Empty<SessionPermissionRule>(), null, TestContext.Current.CancellationToken);
        var fork = h.CreatePlainSession();
        var group = await h.Manager.GetGroupForSessionAsync(root.Id, TestContext.Current.CancellationToken);
        await h.Manager.AddMemberAsync(group!.Id, new SessionGroupMember
        {
            SessionId = fork.Id, Relation = SessionRelation.Fork, ParentSessionId = root.Id
        }, TestContext.Current.CancellationToken);

        var warm = await h.Manager.ListChildrenAsync(root.Id, TestContext.Current.CancellationToken);
        warm.Select(s => s.Id).Should().Equal(child.Id);

        var cold = h.NewColdManager();
        var coldResult = await cold.ListChildrenAsync(root.Id, TestContext.Current.CancellationToken);
        coldResult.Select(s => s.Id).Should().Equal(child.Id);
    }

    [Fact]
    public async Task ListAnchorsAsync_ShouldMaterializeGroupsForUngroupedSessions()
    {
        using var h = new SessionGroupTestHarness();
        var s1 = h.CreateRoot();
        var s2 = h.CreateRoot();
        await h.Sessions.SaveAsync(s1.Id);
        await h.Sessions.SaveAsync(s2.Id);

        var cold = h.NewColdManager();
        var anchors = await cold.ListAnchorsAsync(ct: TestContext.Current.CancellationToken);

        anchors.Select(a => a.Id).Should().BeEquivalentTo(new[] { s1.Id, s2.Id });
        (await h.GroupStore.FindBySessionAsync(s1.Id, TestContext.Current.CancellationToken)).Should().NotBeNull();
        (await h.GroupStore.FindBySessionAsync(s2.Id, TestContext.Current.CancellationToken)).Should().NotBeNull();
    }

    [Fact]
    public async Task GetParentAsync_ShouldReturnParentMemberId()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot();
        var child = await h.Manager.CreateChildAsync(
            root.Id, "task", "子任务", Array.Empty<SessionPermissionRule>(), null, TestContext.Current.CancellationToken);

        (await h.Manager.GetParentAsync(child.Id, TestContext.Current.CancellationToken)).Should().Be(root.Id);
        (await h.Manager.GetParentAsync(root.Id, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task ListAnchorsAsync_ShouldExcludeArchivedAnchors()
    {
        using var h = new SessionGroupTestHarness();
        var active = h.CreateRoot();
        var archived = h.CreateRoot();
        await h.Sessions.SaveAsync(active.Id);
        await h.Sessions.SaveAsync(archived.Id);
        await h.Manager.EnsureForSessionAsync(active.Id, TestContext.Current.CancellationToken);
        await h.Manager.EnsureForSessionAsync(archived.Id, TestContext.Current.CancellationToken);

        h.Sessions.Get(archived.Id)!.IsArchived = true;
        await h.Sessions.SaveAsync(archived.Id);

        var anchors = await h.NewColdManager().ListAnchorsAsync(ct: TestContext.Current.CancellationToken);

        anchors.Select(a => a.Id).Should().Contain(active.Id);
        anchors.Select(a => a.Id).Should().NotContain(archived.Id);
    }

    [Fact]
    public async Task RemoveMemberAsync_WhenAnchorRemoved_ShouldFallbackToForkAndUpdateGroupTitle()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot("根标题");
        var group = await h.Manager.EnsureForSessionAsync(root.Id, TestContext.Current.CancellationToken);

        // Fork（备份）先入组，Order 更小；随后加入 Child 成员（非 Fork）
        var fork = h.CreatePlainSession();
        fork.Title = "备份标题";
        await h.Manager.AddMemberAsync(group.Id, new SessionGroupMember
        {
            SessionId = fork.Id, Relation = SessionRelation.Fork, ParentSessionId = root.Id
        }, TestContext.Current.CancellationToken);
        var child = await h.Manager.CreateChildAsync(
            root.Id, "task", "子标题", Array.Empty<SessionPermissionRule>(), null, TestContext.Current.CancellationToken);

        await h.Manager.RemoveMemberAsync(group.Id, root.Id, TestContext.Current.CancellationToken);

        // 新语义：优先 (d) 非 Fork 且非 Child → 无；再 (e) Fork 兜底 → fork（排除 Child）
        var reloaded = await h.Manager.GetGroupAsync(group.Id, TestContext.Current.CancellationToken);
        var anchor = reloaded!.Members.Single(m => m.IsAnchor);
        anchor.SessionId.Should().Be(fork.Id);
        anchor.Relation.Should().Be(SessionRelation.None);
        reloaded.Title.Should().Be("备份标题");
    }

    [Fact]
    public async Task RemoveMemberAsync_WhenOnlyForkMembersRemain_ShouldPromoteForkAndUpdateGroupTitle()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot("根标题");
        var group = await h.Manager.EnsureForSessionAsync(root.Id, TestContext.Current.CancellationToken);

        var fork1 = h.CreatePlainSession();
        fork1.Title = "备份一";
        var fork2 = h.CreatePlainSession();
        fork2.Title = "备份二";
        await h.Manager.AddMemberAsync(group.Id, new SessionGroupMember
        {
            SessionId = fork1.Id, Relation = SessionRelation.Fork, ParentSessionId = root.Id
        }, TestContext.Current.CancellationToken);
        await h.Manager.AddMemberAsync(group.Id, new SessionGroupMember
        {
            SessionId = fork2.Id, Relation = SessionRelation.Fork, ParentSessionId = root.Id
        }, TestContext.Current.CancellationToken);

        await h.Manager.RemoveMemberAsync(group.Id, root.Id, TestContext.Current.CancellationToken);

        var reloaded = await h.Manager.GetGroupAsync(group.Id, TestContext.Current.CancellationToken);
        var anchor = reloaded!.Members.Single(m => m.IsAnchor);
        anchor.SessionId.Should().Be(fork1.Id);
        anchor.Relation.Should().Be(SessionRelation.None);
        reloaded.Title.Should().Be("备份一");
    }

    [Fact]
    public async Task AddMemberAsync_NonAnchorChildWithNonSubAgentKind_ShouldThrow()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot();
        var group = await h.Manager.EnsureForSessionAsync(root.Id, TestContext.Current.CancellationToken);
        var plain = h.CreatePlainSession(SessionKind.Root);

        var act = () => h.Manager.AddMemberAsync(group.Id, new SessionGroupMember
        {
            SessionId = plain.Id, Relation = SessionRelation.Child, ParentSessionId = root.Id
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AddMemberAsync_AnchorWithNonNoneRelation_ShouldThrow()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot();
        var group = await h.Manager.EnsureForSessionAsync(root.Id, TestContext.Current.CancellationToken);
        var sub = h.CreatePlainSession(SessionKind.SubAgent);

        var act = () => h.Manager.AddMemberAsync(group.Id, new SessionGroupMember
        {
            SessionId = sub.Id, Relation = SessionRelation.Child, IsAnchor = true, ParentSessionId = root.Id
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AddMemberAsync_NonAnchorChildWithSubAgentKind_ShouldNotThrow()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot();
        var group = await h.Manager.EnsureForSessionAsync(root.Id, TestContext.Current.CancellationToken);
        var sub = h.CreatePlainSession(SessionKind.SubAgent);

        var act = () => h.Manager.AddMemberAsync(group.Id, new SessionGroupMember
        {
            SessionId = sub.Id, Relation = SessionRelation.Child, ParentSessionId = root.Id
        });

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task AddMemberAsync_NonAnchorSubAgentWithForkRelation_ShouldThrow()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot();
        var group = await h.Manager.EnsureForSessionAsync(root.Id, TestContext.Current.CancellationToken);
        var sub = h.CreatePlainSession(SessionKind.SubAgent);

        var act = () => h.Manager.AddMemberAsync(group.Id, new SessionGroupMember
        {
            SessionId = sub.Id, Relation = SessionRelation.Fork, ParentSessionId = root.Id
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AddMemberAsync_NonAnchorSubAgentWithHandoffSuccessorRelation_ShouldThrow()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot();
        var group = await h.Manager.EnsureForSessionAsync(root.Id, TestContext.Current.CancellationToken);
        var sub = h.CreatePlainSession(SessionKind.SubAgent);

        var act = () => h.Manager.AddMemberAsync(group.Id, new SessionGroupMember
        {
            SessionId = sub.Id, Relation = SessionRelation.HandoffSuccessor, ParentSessionId = root.Id
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ReadApis_ShouldReturnSnapshots_NotLiveGroup()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot();
        var group = await h.Manager.EnsureForSessionAsync(root.Id, TestContext.Current.CancellationToken);
        var fork = h.CreatePlainSession();

        var byId = await h.Manager.GetGroupAsync(group.Id, TestContext.Current.CancellationToken);
        var bySession = await h.Manager.GetGroupForSessionAsync(root.Id, TestContext.Current.CancellationToken);

        byId!.Members.Should().ContainSingle();
        bySession!.Members.Should().ContainSingle();

        await h.Manager.AddMemberAsync(group.Id, new SessionGroupMember
        {
            SessionId = fork.Id, Relation = SessionRelation.Fork, ParentSessionId = root.Id
        }, TestContext.Current.CancellationToken);

        // 快照不得被后续组内变更回写
        byId.Members.Should().ContainSingle();
        bySession.Members.Should().ContainSingle();
    }

    [Fact]
    public async Task ConcurrentReads_DuringAdds_ShouldNotThrow()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot();
        var group = await h.Manager.EnsureForSessionAsync(root.Id, TestContext.Current.CancellationToken);
        var forks = Enumerable.Range(0, 300).Select(_ => h.CreatePlainSession()).ToArray();

        var writer = Task.Run(async () =>
        {
            foreach (var f in forks)
            {
                await h.Manager.AddMemberAsync(group.Id, new SessionGroupMember
                {
                    SessionId = f.Id, Relation = SessionRelation.Fork, ParentSessionId = root.Id
                });
            }
        }, TestContext.Current.CancellationToken);

        var readers = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 400; i++)
            {
                var members = await h.Manager.ListMembersAsync(group.Id);
                members.Should().NotBeNull();
                await h.Manager.GetParentAsync(forks[0].Id);
                var snapshot = await h.Manager.GetGroupAsync(group.Id);
                snapshot.Should().NotBeNull();
            }
        })).ToArray();

        await Task.WhenAll(readers.Append(writer));

        (await h.Manager.ListMembersAsync(group.Id, TestContext.Current.CancellationToken)).Should().HaveCount(forks.Length + 1);
    }

    [Fact]
    public async Task ClearCache_ShouldForceReloadFromStore()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot();
        var group = await h.Manager.EnsureForSessionAsync(root.Id, TestContext.Current.CancellationToken);

        await h.GroupStore.DeleteAsync(group.Id, TestContext.Current.CancellationToken);
        (await h.Manager.GetGroupAsync(group.Id, TestContext.Current.CancellationToken)).Should().NotBeNull();

        h.Manager.ClearCache();

        (await h.Manager.GetGroupAsync(group.Id, TestContext.Current.CancellationToken)).Should().BeNull();
    }
}

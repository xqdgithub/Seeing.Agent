using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Session.Tests.Management;

/// <summary>
/// 锚点归一（<c>NormalizeAnchor</c>）+ 交接原子迁移 + 关系感知删除的行为验证。
/// </summary>
public class SessionGroupAnchorNormalizationTests
{
    [Fact]
    public async Task Handoff_ShouldPromoteSuccessorAsAnchor_WithSuccessorRelation()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot("根");
        var group = await h.Manager.EnsureForSessionAsync(root.Id);

        var successor = await h.Manager.CreateHandoffSuccessorAsync(root.Id, null, "后继", null);

        var g = await h.Manager.GetGroupAsync(group.Id);
        var anchor = g!.Members.Single(m => m.IsAnchor);
        anchor.SessionId.Should().Be(successor.Id);
        anchor.Relation.Should().Be(SessionRelation.HandoffSuccessor);
        g.Members.Single(m => m.SessionId == root.Id).Relation
            .Should().Be(SessionRelation.HandoffPredecessor);
        g.AnchorSessionId.Should().Be(successor.Id);
        g.ActiveSessionId.Should().Be(successor.Id);
    }

    [Fact]
    public async Task DeleteFork_ShouldKeepAnchorUnchanged()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot("根");
        var group = await h.Manager.EnsureForSessionAsync(root.Id);
        var backup = await h.Manager.CreateBackupForkAsync(root.Id, "trim-backup t0");
        var anchorBefore = (await h.Manager.GetGroupAsync(group.Id))!.AnchorSessionId;

        await h.Manager.RemoveMemberAsync(group.Id, backup.Id);

        (await h.Manager.GetGroupAsync(group.Id))!.AnchorSessionId.Should().Be(anchorBefore);
    }

    [Fact]
    public async Task Handoff_SourceNotAnchor_ShouldThrow()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot();
        var group = await h.Manager.EnsureForSessionAsync(root.Id);
        await h.Manager.CreateHandoffSuccessorAsync(root.Id, null, "后继", null); // root 变为 Predecessor

        var act = async () => await h.Manager.CreateHandoffSuccessorAsync(root.Id, null, "再次", null);
        await act.Should().ThrowAsync<InvalidOperationException>(); // 源必须为当前锚点
    }

    [Fact]
    public async Task Handoff_ShouldNotPublishIntermediateSnapshot()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot();
        var group = await h.Manager.EnsureForSessionAsync(root.Id);
        var versions = new List<long>();
        h.Manager.Changed += (_, e) => versions.Add(e.Group.Version);

        var successor = await h.Manager.CreateHandoffSuccessorAsync(root.Id, null, "后继", null);

        versions.Should().HaveCount(1);                       // 仅一次发布
        versions[0].Should().Be(2);                           // 单次 +1（EnsureForSession=1）
        var g = await h.Manager.GetGroupAsync(group.Id);
        g!.Members.Single(m => m.IsAnchor).SessionId.Should().Be(successor.Id);
    }

    [Fact]
    public async Task DeleteHeadNode_ShouldReparentSuccessorAndNormalizeAnchor()
    {
        using var h = new SessionGroupTestHarness();
        var a = h.CreateRoot("A");
        var group = await h.Manager.EnsureForSessionAsync(a.Id);
        var b = await h.Manager.CreateHandoffSuccessorAsync(a.Id, null, "B", null); // A→B, B 锚点

        await h.Manager.RemoveMemberAsync(group.Id, a.Id);

        var g = await h.Manager.GetGroupAsync(group.Id);
        var bMember = g!.Members.Single(m => m.SessionId == b.Id);
        bMember.ParentSessionId.Should().BeNull();
        bMember.IsAnchor.Should().BeTrue();
        bMember.Relation.Should().Be(SessionRelation.None);   // 删链首后 parent=null → 归一为 None
        g.AnchorSessionId.Should().Be(b.Id);
    }

    [Fact]
    public async Task DeleteMiddleNode_ShouldReparentSuccessorAndDerivedChildren()
    {
        using var h = new SessionGroupTestHarness();
        var a = h.CreateRoot("A");
        var group = await h.Manager.EnsureForSessionAsync(a.Id);
        var b = await h.Manager.CreateHandoffSuccessorAsync(a.Id, null, "B", null);
        var c = await h.Manager.CreateHandoffSuccessorAsync(b.Id, null, "C", null); // A→B→C
        var fork = await h.Manager.CreateBackupForkAsync(b.Id, "trim-backup t1");

        await h.Manager.RemoveMemberAsync(group.Id, b.Id);

        var g = await h.Manager.GetGroupAsync(group.Id);
        g!.Members.Single(m => m.SessionId == c.Id).ParentSessionId.Should().Be(a.Id); // 主线后继重挂
        g.Members.Single(m => m.SessionId == fork.Id).ParentSessionId.Should().Be(a.Id); // 派生重挂
        g.Members.Single(m => m.IsAnchor).SessionId.Should().Be(c.Id);                    // 锚点保持
    }

    [Fact]
    public async Task DeleteAnchor_WhenOnlyChildRemains_ShouldKeepChildAsDegenerateAnchor()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot("根");
        var group = await h.Manager.EnsureForSessionAsync(root.Id);
        var child = await h.Manager.CreateChildAsync(
            root.Id, "task", "子", Array.Empty<SessionPermissionRule>(), null);

        await h.Manager.RemoveMemberAsync(group.Id, root.Id);

        var g = await h.Manager.GetGroupAsync(group.Id);
        var anchor = g!.Members.Single(m => m.IsAnchor);
        anchor.SessionId.Should().Be(child.Id);                 // (f) 仅剩 Child 的退化兜底
        anchor.Relation.Should().Be(SessionRelation.Child);     // 保持 Child（避免 SubAgent+None 非法态）
        g.AnchorSessionId.Should().Be(child.Id);
    }

    [Fact]
    public async Task RemoveSession_DeleteMiddleNode_ShouldReparentSuccessorAndFork()
    {
        using var h = new SessionGroupTestHarness();
        var a = h.CreateRoot("A");
        var group = await h.Manager.EnsureForSessionAsync(a.Id);
        var b = await h.Manager.CreateHandoffSuccessorAsync(a.Id, null, "B", null); // A→B
        var c = await h.Manager.CreateHandoffSuccessorAsync(b.Id, null, "C", null); // A→B→C
        var fork = await h.Manager.CreateBackupForkAsync(b.Id, "trim-backup t1");
        var child = await h.Manager.CreateChildAsync(
            b.Id, "task", "子", Array.Empty<SessionPermissionRule>(), null);

        await h.Manager.RemoveSessionAsync(b.Id);

        var g = await h.Manager.GetGroupAsync(group.Id);
        g!.Members.Should().NotContain(m => m.SessionId == b.Id);
        g.Members.Should().NotContain(m => m.SessionId == child.Id);            // Child 子树随删
        g.Members.Single(m => m.SessionId == c.Id).ParentSessionId.Should().Be(a.Id);   // 主线后继重挂
        g.Members.Single(m => m.SessionId == fork.Id).ParentSessionId.Should().Be(a.Id); // 派生重挂
        g.Members.Single(m => m.IsAnchor).SessionId.Should().Be(c.Id);           // 锚点保持
        g.AnchorSessionId.Should().Be(c.Id);
    }

    [Fact]
    public async Task RemoveSession_DeleteAnchorWithFork_ShouldReparentForkToAnchorParent()
    {
        using var h = new SessionGroupTestHarness();
        var a = h.CreateRoot("A");
        var group = await h.Manager.EnsureForSessionAsync(a.Id);
        var b = await h.Manager.CreateHandoffSuccessorAsync(a.Id, null, "B", null); // A→B，B 锚点
        var backup = await h.Manager.CreateBackupForkAsync(b.Id, "trim-backup t1");

        await h.Manager.RemoveSessionAsync(b.Id);

        var g = await h.Manager.GetGroupAsync(group.Id);
        g!.Members.Single(m => m.SessionId == backup.Id).ParentSessionId.Should().Be(a.Id);
        g.Members.Single(m => m.IsAnchor).SessionId.Should().Be(a.Id);
        g.AnchorSessionId.Should().Be(a.Id);
    }

    /// <summary>
    /// 三层 Child 子树（C1→C2→C3）删除祖先 C1 时，挂在 C3 下的派生会话
    /// 应重挂到最近的存活祖先（根），不得指向删除集内的 C2/C3。
    /// </summary>
    [Fact]
    public async Task RemoveSession_DeleteNestedChildSubtree_ShouldReparentDerivedToLiveAncestor()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot("R");
        var group = await h.Manager.EnsureForSessionAsync(root.Id);
        var c1 = await h.Manager.CreateChildAsync(
            root.Id, "task", "C1", Array.Empty<SessionPermissionRule>(), null);
        var c2 = await h.Manager.CreateChildAsync(
            c1.Id, "task", "C2", Array.Empty<SessionPermissionRule>(), null);
        var c3 = await h.Manager.CreateChildAsync(
            c2.Id, "task", "C3", Array.Empty<SessionPermissionRule>(), null);
        var fork = await h.Manager.CreateBackupForkAsync(c3.Id, "trim-backup t1");

        await h.Manager.RemoveSessionAsync(c1.Id);

        var g = await h.Manager.GetGroupAsync(group.Id);
        g!.Members.Should().NotContain(m => m.SessionId == c1.Id);
        g.Members.Should().NotContain(m => m.SessionId == c2.Id);
        g.Members.Should().NotContain(m => m.SessionId == c3.Id);
        g.Members.Single(m => m.SessionId == fork.Id).ParentSessionId.Should().Be(root.Id);
    }
}

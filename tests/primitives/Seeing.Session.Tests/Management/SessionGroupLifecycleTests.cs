using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Session.Tests.Management;

public class SessionGroupLifecycleTests
{
    [Fact]
    public async Task CreateRootAsync_ShouldCreateGroupAndAssignGroupId()
    {
        using var h = new SessionGroupTestHarness();

        var root = await h.Manager.CreateRootAsync(
            partitionId: "p1", agent: "build", scenario: "code",
            title: "标题", workingDirectory: @"E:\work");

        root.Title.Should().Be("标题");
        root.WorkingDirectory.Should().Be(@"E:\work");
        root.Kind.Should().Be(SessionKind.Root);
        root.GroupId.Should().NotBeNullOrEmpty();
        h.Sessions.Get(root.Id).Should().BeSameAs(root);

        var group = await h.Manager.GetGroupAsync(root.GroupId!);
        group.Should().NotBeNull();
        group!.AnchorSessionId.Should().Be(root.Id);
        group.PartitionId.Should().Be("p1");
        group.Members.Should().ContainSingle();
        group.Members[0].IsAnchor.Should().BeTrue();
        group.Members[0].Relation.Should().Be(SessionRelation.None);
    }

    [Fact]
    public async Task CreateChildAsync_ShouldJoinParentGroupWithRelationAndSnapshot()
    {
        using var h = new SessionGroupTestHarness();
        var root = await h.Manager.CreateRootAsync("p1", "build", "code", "根", null);
        var snapshot = new[]
        {
            new SessionPermissionRule { Kind = "Tool", Pattern = "*", Effect = "Allow", Priority = 1 }
        };

        var child = await h.Manager.CreateChildAsync(root.Id, "task", "子任务", snapshot, "code");

        child.Kind.Should().Be(SessionKind.SubAgent);
        child.Title.Should().Be("子任务");
        child.PartitionId.Should().Be("p1");
        child.PermissionSnapshot.Should().HaveCount(1);
        child.PermissionSnapshot[0].Pattern.Should().Be("*");
        child.GroupId.Should().Be(root.GroupId);

        var group = await h.Manager.GetGroupForSessionAsync(child.Id);
        group!.Id.Should().Be(root.GroupId);
        var member = group.Members.Single(m => m.SessionId == child.Id);
        member.Relation.Should().Be(SessionRelation.Child);
        member.ParentSessionId.Should().Be(root.Id);
        member.IsAnchor.Should().BeFalse();
    }

    [Fact]
    public async Task ForkSessionAsync_FromRoot_ShouldJoinSourceGroupAsFork()
    {
        using var h = new SessionGroupTestHarness();
        var root = await h.Manager.CreateRootAsync("p1", "build", "code", "根", null);
        root.AddMessage(new SessionMessage { Id = "m1", Role = "user", Content = "hi", CreatedAt = DateTime.UtcNow });
        h.Sessions.Register(root);

        var fork = await h.Manager.ForkSessionAsync(root.Id, "分支");

        fork.Kind.Should().Be(SessionKind.Root);
        fork.Title.Should().Be("分支");
        fork.Messages.Should().HaveCount(1);
        fork.GroupId.Should().Be(root.GroupId);

        var group = await h.Manager.GetGroupForSessionAsync(fork.Id);
        group!.Id.Should().Be(root.GroupId);
        var member = group.Members.Single(m => m.SessionId == fork.Id);
        member.Relation.Should().Be(SessionRelation.Fork);
        member.ParentSessionId.Should().Be(root.Id);
    }

    [Fact]
    public async Task ForkSessionAsync_FromSubAgent_ShouldCreateIndependentGroup()
    {
        using var h = new SessionGroupTestHarness();
        var root = await h.Manager.CreateRootAsync("p1", "build", "code", "根", null);
        var child = await h.Manager.CreateChildAsync(
            root.Id, "task", "子任务", Array.Empty<SessionPermissionRule>(), null);

        var fork = await h.Manager.ForkSessionAsync(child.Id, "分支");

        fork.Kind.Should().Be(SessionKind.Root);
        var parentGroup = await h.Manager.GetGroupForSessionAsync(child.Id);
        var forkGroup = await h.Manager.GetGroupForSessionAsync(fork.Id);

        forkGroup.Should().NotBeNull();
        forkGroup!.Id.Should().NotBe(parentGroup!.Id);
        forkGroup.AnchorSessionId.Should().Be(fork.Id);
        forkGroup.Members.Should().ContainSingle();
        forkGroup.Members[0].Relation.Should().Be(SessionRelation.None);
        forkGroup.Members[0].ParentSessionId.Should().Be(child.Id);
    }

    [Fact]
    public async Task CreateBackupForkAsync_ShouldAlwaysJoinSourceGroupWithLabel()
    {
        using var h = new SessionGroupTestHarness();
        var root = await h.Manager.CreateRootAsync("p1", "build", "code", "根", null);
        var child = await h.Manager.CreateChildAsync(
            root.Id, "task", "子任务", Array.Empty<SessionPermissionRule>(), null);

        var backupFromRoot = await h.Manager.CreateBackupForkAsync(root.Id, "root-backup");
        var backupFromChild = await h.Manager.CreateBackupForkAsync(child.Id, "child-backup");

        backupFromRoot.Kind.Should().Be(SessionKind.Root);
        var rootGroup = await h.Manager.GetGroupForSessionAsync(root.Id);
        var childGroup = await h.Manager.GetGroupForSessionAsync(child.Id);

        (await h.Manager.GetGroupForSessionAsync(backupFromRoot.Id))!.Id.Should().Be(rootGroup!.Id);
        // 子会话处于父组中；备份始终入源所在组（而非独立新组）
        (await h.Manager.GetGroupForSessionAsync(backupFromChild.Id))!.Id.Should().Be(childGroup!.Id);

        var memberFromRoot = rootGroup.Members.Single(m => m.SessionId == backupFromRoot.Id);
        memberFromRoot.Relation.Should().Be(SessionRelation.Fork);
        memberFromRoot.Label.Should().Be("root-backup");

        var memberFromChild = childGroup.Members.Single(m => m.SessionId == backupFromChild.Id);
        memberFromChild.Relation.Should().Be(SessionRelation.Fork);
        memberFromChild.ParentSessionId.Should().Be(child.Id);
        memberFromChild.Label.Should().Be("child-backup");
    }

    [Fact]
    public async Task CreateHandoffSuccessorAsync_ShouldJoinGroupActivateAndInheritConfig()
    {
        using var h = new SessionGroupTestHarness();
        var root = await h.Manager.CreateRootAsync("p1", "build", "code", "根", @"E:\work");
        root.SelectedModel = "openai/gpt-4o";
        root.SelectedThinkingEffort = "high";
        root.AddMessage(new SessionMessage { Id = "m1", Role = "user", Content = "hi", CreatedAt = DateTime.UtcNow });
        h.Sessions.Register(root);

        var successor = await h.Manager.CreateHandoffSuccessorAsync(root.Id, null, "接续", "code");

        successor.Kind.Should().Be(SessionKind.Root);
        successor.Messages.Should().BeEmpty();
        successor.PartitionId.Should().Be("p1");
        successor.SelectedAgent.Should().Be("build");
        successor.SelectedModel.Should().Be("openai/gpt-4o");
        successor.SelectedThinkingEffort.Should().Be("high");
        successor.WorkingDirectory.Should().Be(@"E:\work");
        successor.Scenario.Should().Be("code");

        var group = await h.Manager.GetGroupForSessionAsync(successor.Id);
        group!.Id.Should().Be(root.GroupId);
        group.ActiveSessionId.Should().Be(successor.Id);
        var member = group.Members.Single(m => m.SessionId == successor.Id);
        member.Relation.Should().Be(SessionRelation.HandoffSuccessor);
        member.ParentSessionId.Should().Be(root.Id);
    }
}

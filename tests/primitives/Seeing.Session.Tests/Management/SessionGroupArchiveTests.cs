using FluentAssertions;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Session.Tests.Management;

public class SessionGroupArchiveTests
{
    [Fact]
    public async Task ArchiveGroupAsync_ShouldArchiveAllMembers()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot("g-root");
        await h.Manager.EnsureForSessionAsync(root.Id);

        var child = h.CreatePlainSession(SessionKind.SubAgent);
        child.Title = "child";
        await h.SessionStore.SaveAsync(child);
        var group = await h.Manager.GetGroupForSessionAsync(root.Id);
        await h.Manager.AddMemberAsync(group!.Id, new SessionGroupMember
        {
            SessionId = child.Id,
            Relation = SessionRelation.Child,
            ParentSessionId = root.Id
        });

        var archived = await h.Manager.ArchiveGroupAsync(group.Id);

        archived.Should().BeTrue();
        (await h.SessionStore.LoadAsync(root.Id)).Should().BeNull();
        (await h.SessionStore.LoadAsync(child.Id)).Should().BeNull();

        var archivedSummaries = new List<SessionSummary>();
        await foreach (var s in h.SessionStore.ListArchivedAsync())
            archivedSummaries.Add(s);
        archivedSummaries.Should().HaveCount(2);
    }

    [Fact]
    public async Task RestoreGroupAsync_ShouldRestoreAllMembers()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot("g-root");
        var group = await h.Manager.EnsureForSessionAsync(root.Id);

        await h.Manager.ArchiveGroupAsync(group.Id);
        var restored = await h.Manager.RestoreGroupAsync(group.Id);

        restored.Should().BeTrue();
        var loaded = await h.SessionStore.LoadAsync(root.Id);
        loaded.Should().NotBeNull();
        loaded!.IsArchived.Should().BeFalse();
    }

    [Fact]
    public async Task ListAnchorSummariesAsync_ShouldNotMaterializeGroups()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot("anchor");
        await h.SessionStore.SaveAsync(root);

        // 冷管理器：不物化任何组
        var cold = h.NewColdManager();
        var summaries = await cold.ListAnchorSummariesAsync();

        summaries.Should().ContainSingle(s => s.Id == root.Id);
        // 未入组会话不写盘建组
        (await h.GroupStore.FindBySessionAsync(root.Id)).Should().BeNull();
    }

    [Fact]
    public async Task ListAnchorSummariesAsync_ShouldExcludeArchived()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot("archived-anchor");
        await h.SessionStore.SaveAsync(root);
        await h.SessionStore.ArchiveAsync(root.Id);

        var summaries = await h.NewColdManager().ListAnchorSummariesAsync();

        summaries.Should().BeEmpty();
    }
}

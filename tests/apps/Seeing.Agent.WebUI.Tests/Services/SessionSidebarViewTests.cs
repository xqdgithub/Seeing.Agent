using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Tests.Services;

public class SessionSidebarViewTests
{
    private static SidebarEntry Entry(
        string id,
        SessionRelation relation,
        bool anchor = false,
        bool executing = false,
        string? parent = null,
        string? label = null,
        bool active = false)
        => new(id, $"标题-{id}", relation, anchor, executing, parent, label, active);

    [Fact]
    public void Build_AllThreeSections_ShouldReturnInFixedOrder()
    {
        var entries = new[]
        {
            Entry("child", SessionRelation.Child, parent: "anchor"),
            Entry("fork", SessionRelation.Fork, parent: "anchor"),
            Entry("prev", SessionRelation.HandoffPredecessor),
        };

        var sections = SessionSidebarView.Build(entries);

        sections.Select(s => s.Section).Should().Equal(
            SessionSection.MainLineHistory,
            SessionSection.Derived,
            SessionSection.Children);
        sections.Select(s => s.Title).Should().Equal("主线历史", "派生", "子会话");
    }

    [Fact]
    public void Build_EmptySections_ShouldBeOmitted()
    {
        var entries = new[]
        {
            Entry("fork", SessionRelation.Fork, parent: "anchor"),
        };

        var sections = SessionSidebarView.Build(entries);

        sections.Should().ContainSingle();
        sections[0].Section.Should().Be(SessionSection.Derived);
    }

    [Fact]
    public void Build_NoEntries_ShouldReturnEmpty()
    {
        SessionSidebarView.Build(Array.Empty<SidebarEntry>()).Should().BeEmpty();
    }

    [Fact]
    public void Build_WithinSection_ShouldFloatExecutingToTopKeepingInputOrder()
    {
        var entries = new[]
        {
            Entry("A", SessionRelation.HandoffPredecessor),
            Entry("B", SessionRelation.HandoffPredecessor, executing: true),
            Entry("C", SessionRelation.HandoffPredecessor),
            Entry("D", SessionRelation.HandoffPredecessor, executing: true),
        };

        var section = SessionSidebarView.Build(entries).Single();

        section.Entries.Select(e => e.SessionId).Should().Equal("B", "D", "A", "C");
    }

    [Fact]
    public void Build_MainLineHistory_ShouldContainPredecessorAndLegacySuccessorAndAnchor()
    {
        var entries = new[]
        {
            Entry("anchor", SessionRelation.None, anchor: true),
            Entry("legacy-succ", SessionRelation.HandoffSuccessor, parent: "anchor"),
            Entry("prev", SessionRelation.HandoffPredecessor),
        };

        var section = SessionSidebarView.Build(entries).Single(s => s.Section == SessionSection.MainLineHistory);

        section.Entries.Select(e => e.SessionId).Should().Equal("anchor", "legacy-succ", "prev");
    }

    [Fact]
    public void Build_DegenerateChildAnchor_ShouldBelongToChildrenSection()
    {
        var entries = new[]
        {
            Entry("only-child", SessionRelation.Child, anchor: true),
        };

        var section = SessionSidebarView.Build(entries).Single();

        section.Section.Should().Be(SessionSection.Children);
    }

    [Fact]
    public void Build_ShouldPreserveEntryPayload()
    {
        var entry = new SidebarEntry(
            "s1", "会话", SessionRelation.Fork, IsAnchor: false, IsExecuting: true,
            ParentSessionId: "p1", Label: "trim-backup-x", IsActive: true);

        var built = SessionSidebarView.Build(new[] { entry }).Single().Entries.Single();

        built.Should().Be(entry);
    }
}

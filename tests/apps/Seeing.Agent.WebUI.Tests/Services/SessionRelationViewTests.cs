using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Tests.Services;

public class SessionRelationViewTests
{
    [Fact]
    public void BadgeText_HandoffPredecessorWithoutSuccessor_ShouldReturnHandedOff()
    {
        SessionRelationView.BadgeText(SessionRelation.HandoffPredecessor, isAnchor: false, label: null)
            .Should().Be("已交接");
    }

    [Fact]
    public void BadgeText_HandoffPredecessorWithSuccessor_ShouldReturnHandedOffToTitle()
    {
        SessionRelationView.BadgeText(
                SessionRelation.HandoffPredecessor, isAnchor: false, label: null, successorTitle: "后继 B")
            .Should().Be("已交接 → 后继 B");
    }

    [Fact]
    public void BadgeText_ForkWithTrimBackupLabel_ShouldReturnBackup()
    {
        SessionRelationView.BadgeText(SessionRelation.Fork, isAnchor: false, label: "trim-backup-2026")
            .Should().Be("备份");
    }

    [Fact]
    public void BadgeText_ForkWithoutLabel_ShouldReturnFork()
    {
        SessionRelationView.BadgeText(SessionRelation.Fork, isAnchor: false, label: null)
            .Should().Be("分叉");
    }

    [Fact]
    public void BadgeText_Child_ShouldReturnChildReadOnly()
    {
        SessionRelationView.BadgeText(SessionRelation.Child, isAnchor: false, label: null)
            .Should().Be("子代理（只读）");
    }

    [Fact]
    public void BadgeText_HandoffSuccessorWithAnchor_ShouldReturnMainLineWithHandoffHint()
    {
        SessionRelationView.BadgeText(SessionRelation.HandoffSuccessor, isAnchor: true, label: null)
            .Should().Be("主线 · 交接后继");
    }

    [Fact]
    public void BadgeText_NoneWithAnchor_ShouldReturnPlainMainLine()
    {
        SessionRelationView.BadgeText(SessionRelation.None, isAnchor: true, label: null)
            .Should().Be("主线");
    }

    [Fact]
    public void BadgeText_ChildWithAnchor_ShouldReturnChildReadOnlyMissingMainLine()
    {
        SessionRelationView.BadgeText(SessionRelation.Child, isAnchor: true, label: null)
            .Should().Be("子代理（只读）·主线缺失");
    }

    [Fact]
    public void Section_Fork_ShouldReturnDerived()
    {
        SessionRelationView.Section(SessionRelation.Fork, isAnchor: false)
            .Should().Be(SessionSection.Derived);
    }

    [Fact]
    public void Section_Child_ShouldReturnChildren()
    {
        SessionRelationView.Section(SessionRelation.Child, isAnchor: false)
            .Should().Be(SessionSection.Children);
    }

    [Fact]
    public void Section_HandoffPredecessor_ShouldReturnMainLineHistory()
    {
        SessionRelationView.Section(SessionRelation.HandoffPredecessor, isAnchor: false)
            .Should().Be(SessionSection.MainLineHistory);
    }

    [Fact]
    public void Section_Anchor_ShouldReturnMainLineHistory()
    {
        SessionRelationView.Section(SessionRelation.None, isAnchor: true)
            .Should().Be(SessionSection.MainLineHistory);
    }
}

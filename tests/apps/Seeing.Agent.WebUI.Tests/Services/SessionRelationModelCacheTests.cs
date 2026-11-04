using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>关系模型与会话分区预计算：构建期算好，重复访问返回同一实例。</summary>
public class SessionRelationModelCacheTests
{
    private static RelationNode Node(
        string id, SessionRelation relation, bool isAnchor, string? parent = null)
        => new(id, id, relation, isAnchor, IsExecuting: false, parent, Label: null, IsActive: false);

    private static RelationTreeModel SampleModel() => RelationTreeModel.Build(new[]
    {
        Node("a", SessionRelation.None, isAnchor: true),
        Node("b", SessionRelation.Child, isAnchor: false, parent: "a"),
        Node("c", SessionRelation.Child, isAnchor: false, parent: "a"),
    });

    private static RelationNode[] FiveHandoffChain()
    {
        var nodes = new List<RelationNode>();
        for (var i = 0; i < 5; i++)
        {
            nodes.Add(new RelationNode(
                $"h{i}",
                $"h{i}",
                i == 4 ? SessionRelation.HandoffSuccessor : SessionRelation.HandoffPredecessor,
                IsAnchor: i == 4,
                IsExecuting: false,
                ParentSessionId: i == 0 ? null : $"h{i - 1}",
                Label: null,
                IsActive: false));
        }
        return nodes.ToArray();
    }

    [Fact]
    public void BarVisible_RepeatedAccess_ShouldReturnSameInstance()
    {
        var model = SampleModel();

        model.BarVisible.Should().BeSameAs(model.BarVisible);
    }

    [Fact]
    public void BarVisible_WithShortMainLine_ShouldContainAnchorAndNoOverflow()
    {
        var model = SampleModel();

        model.BarVisible.Should().Contain(n => n.IsAnchor);
        model.BarOverflow.Should().Be(0);
    }

    [Fact]
    public void BarVisible_WithLongMainLine_ShouldKeepFirstAndAnchor()
    {
        var model = RelationTreeModel.Build(FiveHandoffChain());

        model.BarVisible.Should().HaveCount(2);
        model.BarOverflow.Should().Be(3);
    }

    [Fact]
    public void Rows_RepeatedAccess_ShouldReturnSameInstance()
    {
        var model = SampleModel();

        model.Rows.Should().BeSameAs(model.Rows);
    }

    [Fact]
    public void Rows_ShouldPreserveBuildRowsSemantics()
    {
        var model = SampleModel();

        model.Rows.Should().HaveCount(3);
        model.Rows[0].Depth.Should().Be(0);
        model.Rows.Skip(1).Should().OnlyContain(r => r.Depth == 1);
    }

    [Fact]
    public void SidebarSection_PendingPermissionTotal_ShouldSumEntries()
        => SessionSidebarView.Build([
                new SidebarEntry("a", "A", SessionRelation.None, true, false, null, null, false, 2),
                new SidebarEntry("b", "B", SessionRelation.Child, false, false, "a", null, false, 3)])
            .Sum(s => s.PendingPermissionTotal).Should().Be(5);
}

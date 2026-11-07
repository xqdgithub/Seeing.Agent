using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;

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

    /// <summary>
    /// 顶部关系条只在存在交接链时渲染：纯子会话/纯分叉交给侧栏，避免重复信息占垂直空间。
    /// </summary>
    [Fact]
    public void HasHandoffChain_SingleAnchorWithChildren_ShouldBeFalse()
    {
        var model = SampleModel(); // 1 锚点 + 2 子会话

        model.HasHandoffChain.Should().BeFalse("无交接链，顶部条应让位给侧栏");
        model.HasRelatedSessions.Should().BeTrue("仍有关系，需要抽屉入口");
    }

    [Fact]
    public void HasHandoffChain_WithHandoffChain_ShouldBeTrue()
    {
        var model = RelationTreeModel.Build(FiveHandoffChain());

        model.HasHandoffChain.Should().BeTrue("交接链的链式顺序是顶部条不可替代的价值");
    }

    [Fact]
    public void HasHandoffChain_LoneSession_ShouldBeFalse()
    {
        var model = RelationTreeModel.Build(new[] { Node("a", SessionRelation.None, isAnchor: true) });

        model.HasHandoffChain.Should().BeFalse();
        model.HasRelatedSessions.Should().BeFalse("单一独立会话无任何关系");
    }

    [Fact]
    public void HasHandoffChain_DerivedOnly_ShouldBeFalse()
    {
        var model = RelationTreeModel.Build(new[]
        {
            Node("a", SessionRelation.None, isAnchor: true),
            Node("b", SessionRelation.Fork, isAnchor: false, parent: "a"),
        });

        model.HasHandoffChain.Should().BeFalse("纯分叉不算交接链");
        model.HasRelatedSessions.Should().BeTrue();
    }
}

using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Tests.Services;

public class SessionRelationModelTests
{
    private static RelationNode Node(
        string id,
        SessionRelation relation,
        string? parent = null,
        bool anchor = false,
        string? label = null,
        bool executing = false,
        bool active = false)
        => new(id, $"标题-{id}", relation, anchor, executing, parent, label, active);

    [Fact]
    public void Build_ChainABC_ShouldReturnMainLineFromEarliestToAnchor()
    {
        var nodes = new[]
        {
            Node("A", SessionRelation.HandoffPredecessor),
            Node("B", SessionRelation.HandoffPredecessor, parent: "A"),
            Node("C", SessionRelation.HandoffSuccessor, parent: "B", anchor: true),
        };

        var model = RelationTreeModel.Build(nodes);

        model.MainLine.Select(n => n.SessionId).Should().Equal("A", "B", "C");
    }

    [Fact]
    public void Build_LegacyGroup_AnchorFirstWithNonAnchorSuccessor_ShouldIncludeAllInMainLine()
    {
        // 旧组形态：锚点在链首（Relation=None, parent=null），另有一个非锚点 HandoffSuccessor
        var nodes = new[]
        {
            Node("anchor", SessionRelation.None, anchor: true),
            Node("succ", SessionRelation.HandoffSuccessor, parent: "anchor"),
        };

        var model = RelationTreeModel.Build(nodes);

        model.MainLine.Select(n => n.SessionId).Should().Contain("anchor").And.Contain("succ");
        model.MainLine.Should().HaveCount(2);
        model.MainLine[^1].SessionId.Should().Be("anchor");
        model.MainLine[^1].IsAnchor.Should().BeTrue();
    }

    [Fact]
    public void Build_BrokenChain_ShouldFlattenByOrder_NotThrow()
    {
        // 锚点（Order 靠后）的 parent 缺失 → 断链；全部候选按输入（Order）平铺，锚点置末
        var nodes = new[]
        {
            Node("A", SessionRelation.HandoffPredecessor),
            Node("C", SessionRelation.HandoffSuccessor, parent: "B", anchor: true),
        };

        var model = RelationTreeModel.Build(nodes);

        model.MainLine.Select(n => n.SessionId).Should().Equal("A", "C");
        model.MainLine[^1].SessionId.Should().Be("C");
    }

    [Fact]
    public void Build_HandoffPredecessor_ShouldResolveSuccessorTitle()
    {
        var nodes = new[]
        {
            Node("A", SessionRelation.HandoffPredecessor),
            Node("B", SessionRelation.HandoffSuccessor, parent: "A", anchor: true),
        };

        var model = RelationTreeModel.Build(nodes);

        model.MainLine.Single(n => n.SessionId == "A").SuccessorTitle.Should().Be("标题-B");
    }

    [Fact]
    public void Build_ChildAnchor_ShouldNotAppearInChildren()
    {
        // 退化锚点（仅剩 Child 恰为锚点）不得重复出现在孤儿子会话区
        var nodes = new[]
        {
            Node("only-child", SessionRelation.Child, anchor: true),
        };

        var model = RelationTreeModel.Build(nodes);

        model.Children.Should().BeEmpty();
        model.MainLine.Select(n => n.SessionId).Should().Equal("only-child");
    }

    [Fact]
    public void Build_WithForkAndChild_ShouldClassifyAndCount()
    {
        var nodes = new[]
        {
            Node("A", SessionRelation.HandoffSuccessor, anchor: true),
            Node("F1", SessionRelation.Fork, parent: "A"),
            Node("F2", SessionRelation.Fork, parent: "A", label: "trim-backup-2026"),
            Node("C1", SessionRelation.Child, parent: "A"),
        };

        var model = RelationTreeModel.Build(nodes);

        model.Derived.Select(n => n.SessionId).Should().Equal("F1", "F2");
        model.DerivedCount.Should().Be(2);
        model.Children.Select(n => n.SessionId).Should().Equal("C1");
        model.ChildCount.Should().Be(1);
    }

    [Fact]
    public void BarItems_ThreeOrFewer_ShouldReturnAllWithoutOverflow()
    {
        var mainLine = new[]
        {
            Node("A", SessionRelation.HandoffPredecessor),
            Node("B", SessionRelation.HandoffPredecessor, parent: "A"),
            Node("C", SessionRelation.HandoffSuccessor, parent: "B", anchor: true),
        };

        var (visible, overflow) = RelationTreeModel.BarItems(mainLine);

        visible.Select(n => n.SessionId).Should().Equal("A", "B", "C");
        overflow.Should().Be(0);
    }

    [Fact]
    public void BarItems_MoreThanThree_ShouldReturnFirstAndAnchorWithOverflow()
    {
        var mainLine = new[]
        {
            Node("A", SessionRelation.HandoffPredecessor),
            Node("B", SessionRelation.HandoffPredecessor, parent: "A"),
            Node("C", SessionRelation.HandoffPredecessor, parent: "B"),
            Node("D", SessionRelation.HandoffPredecessor, parent: "C"),
            Node("E", SessionRelation.HandoffSuccessor, parent: "D", anchor: true),
        };

        var (visible, overflow) = RelationTreeModel.BarItems(mainLine);

        visible.Select(n => n.SessionId).Should().Equal("A", "E");
        overflow.Should().Be(3);
    }

    [Fact]
    public void Build_NoAnchor_ShouldFallbackToFlatHistoryWithoutThrowing()
    {
        var nodes = new[]
        {
            Node("A", SessionRelation.HandoffPredecessor),
            Node("B", SessionRelation.HandoffPredecessor, parent: "A"),
            Node("F", SessionRelation.Fork, parent: "A"),
        };

        var model = RelationTreeModel.Build(nodes);

        // 无锚点：主线候选按输入顺序平铺（排除 Fork/Child），不抛异常
        model.MainLine.Select(n => n.SessionId).Should().Equal("A", "B");
        model.DerivedCount.Should().Be(1);
    }

    [Fact]
    public void BuildRows_WhenParentIsDerivedNode_ShouldNotMarkSourceMissing()
    {
        // F2 的来源 F1 是派生节点（不在主线）：父存在 → 不得标“来源已删除”
        var nodes = new[]
        {
            Node("A", SessionRelation.HandoffSuccessor, anchor: true),
            Node("F1", SessionRelation.Fork, parent: "A"),
            Node("F2", SessionRelation.Fork, parent: "F1"),
        };

        var rows = RelationTreeModel.Build(nodes).BuildRows();

        rows.Single(r => r.Node.SessionId == "F2").Source.Should().BeNull();
    }

    [Fact]
    public void BuildRows_WhenParentIsChildNode_ShouldNotMarkSourceMissing()
    {
        // C2 的来源 C1 是子会话节点：父存在 → 不得标“来源已删除”
        var nodes = new[]
        {
            Node("A", SessionRelation.HandoffSuccessor, anchor: true),
            Node("C1", SessionRelation.Child, parent: "A"),
            Node("C2", SessionRelation.Child, parent: "C1"),
        };

        var rows = RelationTreeModel.Build(nodes).BuildRows();

        rows.Single(r => r.Node.SessionId == "C2").Source.Should().BeNull();
    }

    [Fact]
    public void BuildRows_WhenParentMissing_ShouldMarkSourceMissing()
    {
        var nodes = new[]
        {
            Node("A", SessionRelation.HandoffSuccessor, anchor: true),
            Node("F", SessionRelation.Fork, parent: "ghost"),
        };

        var rows = RelationTreeModel.Build(nodes).BuildRows();

        rows.Single(r => r.Node.SessionId == "F").Source.Should().Be(RelationTreeModel.SourceMissingText);
    }

    [Fact]
    public void HasRelatedSessions_WhenSingleAnchorOnly_ShouldBeFalse()
    {
        // 单一独立会话（无主线历史、无派生、无子会话）：顶条不渲染
        var model = RelationTreeModel.Build(new[]
        {
            Node("A", SessionRelation.None, anchor: true),
        });

        model.HasRelatedSessions.Should().BeFalse();
    }

    [Fact]
    public void HasRelatedSessions_WhenChildExists_ShouldBeTrue()
    {
        var model = RelationTreeModel.Build(new[]
        {
            Node("A", SessionRelation.None, anchor: true),
            Node("C", SessionRelation.Child, parent: "A"),
        });

        model.HasRelatedSessions.Should().BeTrue();
    }

    [Fact]
    public void HasRelatedSessions_WhenHandoffChain_ShouldBeTrue()
    {
        var model = RelationTreeModel.Build(new[]
        {
            Node("A", SessionRelation.HandoffPredecessor),
            Node("B", SessionRelation.HandoffSuccessor, parent: "A", anchor: true),
        });

        model.HasRelatedSessions.Should().BeTrue();
    }
}

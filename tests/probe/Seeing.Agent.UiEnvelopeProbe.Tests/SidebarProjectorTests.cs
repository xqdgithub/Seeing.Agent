using FluentAssertions;
using Seeing.Agent.UiEnvelopeProbe;

namespace Seeing.Agent.UiEnvelopeProbe.Tests;

/// <summary>
/// 侧栏投影等价性（对照 AppSidebar.razor:295-371）。
/// </summary>
public class SidebarProjectorTests
{
    private static PageContributionProbe Page(
        string id, string route, string title,
        string? icon = "tool", string? group = null, string? groupIcon = null,
        int order = 0, int groupOrder = 0)
        => new()
        {
            Point = "shell.page",
            Id = id,
            Route = route,
            Title = title,
            ViewKey = id,
            Order = order,
            Nav = new NavPlacement
            {
                Icon = icon ?? "tool",
                Group = group,
                GroupIcon = groupIcon,
                GroupOrder = groupOrder
            }
        };

    private static UiContributionEntry E(string owner, PageContributionProbe page) => new(owner, page);

    // ── 规则①：参数化路由不进侧栏（UiContributionVisibility.cs:41）──

    [Fact]
    public void ParameterizedRoute_ShouldBeExcluded()
    {
        var projection = SidebarProjector.Project([
            E("m", Page("p1", "/session/{SessionId}", "会话")),
            E("m", Page("p2", "/session", "会话"))
        ]);

        projection.Visible.Select(v => v.Route).Should().Equal("/session");
    }

    [Fact]
    public void CatchAllRoute_ShouldBeExcluded()
    {
        var projection = SidebarProjector.Project([
            E("m", Page("p1", "/memory/detail/{*FilePath}", "详情"))
        ]);

        projection.Visible.Should().BeEmpty();
    }

    [Fact]
    public void NavNullPage_ShouldBeExcluded()
    {
        var noNav = new PageContributionProbe
        { Point = "shell.page", Id = "p", Route = "/x", Title = "X", ViewKey = "p", Nav = null };

        SidebarProjector.Project([E("m", noNav)]).Visible.Should().BeEmpty();
    }

    // ── 规则②：更短前缀隐藏子路径（UiContributionVisibility.cs:47）──

    [Fact]
    public void ShorterPrefix_ShouldHideSubPath()
    {
        // /memory 存在时 /memory/graph 不进侧栏（26-memory-page-redesign 记录的 bug）
        var projection = SidebarProjector.Project([
            E("memory", Page("p.memory", "/memory", "记忆")),
            E("memory", Page("p.graph", "/memory/graph", "知识图谱"))
        ]);

        projection.Visible.Select(v => v.Route).Should().Equal("/memory");
    }

    [Fact]
    public void NoShorterPrefix_ShouldKeepBoth()
    {
        // 两者 Order 相同 → 按 Title 排序（"工具" < "技能" 按中文序数）
        var projection = SidebarProjector.Project([
            E("m", Page("p1", "/skills", "技能")),
            E("m", Page("p2", "/tools", "工具"))
        ]);

        projection.Visible.Select(v => v.Route).Should().BeEquivalentTo(["/skills", "/tools"]);
        projection.Visible.Should().HaveCount(2);
    }

    // ── 规则③：分组顺序（审查 D12：默认 0 不能排到最前）──

    [Fact]
    public void Groups_ShouldOrderByGroupOrder()
    {
        var projection = SidebarProjector.Project([
            E("m", Page("p1", "/a", "A", group: "控制中心", groupIcon: "control",
                groupOrder: SidebarProjector.GroupOrders.Control)),
            E("m", Page("p2", "/b", "B", group: "工作区", groupIcon: "folder",
                groupOrder: SidebarProjector.GroupOrders.Workspace)),
            E("m", Page("p3", "/c", "C", group: "设置", groupIcon: "setting",
                groupOrder: SidebarProjector.GroupOrders.Settings))
        ]);

        projection.Groups.Select(g => g.Title).Should().Equal("控制中心", "工作区", "设置");
    }

    [Fact]
    public void GroupWithUnspecifiedOrder_ShouldSortLast()
    {
        // 关键：第三方分组默认 GroupOrder = 0，必须排在三个内建分组之后（旧 preferred 数组语义）
        var projection = SidebarProjector.Project([
            E("m", Page("p1", "/a", "A", group: "第三方分组")),
            E("m", Page("p2", "/b", "B", group: "工作区", groupOrder: SidebarProjector.GroupOrders.Workspace))
        ]);

        projection.Groups.Select(g => g.Title).Should().Equal("工作区", "第三方分组");
    }

    [Fact]
    public void Ungrouped_ShouldBeSeparated()
    {
        var projection = SidebarProjector.Project([
            E("m", Page("p1", "/a", "A")),
            E("m", Page("p2", "/b", "B", group: "工作区"))
        ]);

        projection.Ungrouped.Select(v => v.Route).Should().Equal("/a");
        projection.Groups.Should().ContainSingle();
    }

    [Fact]
    public void GroupIcon_ShouldComeFromFirstByOrder()
    {
        var projection = SidebarProjector.Project([
            E("a", Page("p2", "/b", "B", group: "G", groupIcon: "second", order: 20)),
            E("b", Page("p1", "/a", "A", group: "G", groupIcon: "first", order: 10))
        ]);

        projection.Groups.Single().Icon.Should().Be("first");
    }

    [Fact]
    public void ItemsInGroup_ShouldOrderByOrderThenTitle()
    {
        var projection = SidebarProjector.Project([
            E("m", Page("p3", "/c", "C", group: "G", order: 20)),
            E("m", Page("p1", "/a", "A", group: "G", order: 10)),
            E("m", Page("p2", "/b", "B", group: "G", order: 10))
        ]);

        projection.Groups.Single().Items.Select(v => v.Title).Should().Equal("A", "B", "C");
    }

    // ── 规则④：菜单选中键（AppSidebar.razor:347-366）──

    [Theory]
    [InlineData("", "chat")]
    [InlineData("/", "chat")]
    [InlineData("home", "chat")]
    [InlineData("session", "chat")]
    [InlineData("session/abc", "chat")]
    public void SelectedKeys_SpecialPaths_ShouldBeChat(string path, string expected)
    {
        var visible = new List<NavView>
        {
            new("sessions", "/sessions", "会话", "team", "控制中心", null, 10, 10)
        };

        SidebarProjector.SelectedKeys(path, visible).Should().Equal(expected);
    }

    [Fact]
    public void SelectedKeys_ExactRoute_ShouldMatch()
    {
        var visible = new List<NavView>
        {
            new("memory", "/memory", "记忆", "database", "工作区", null, 50, 20)
        };

        SidebarProjector.SelectedKeys("memory", visible).Should().Equal("memory");
    }

    [Fact]
    public void SelectedKeys_PrefixRoute_ShouldMatchParent()
    {
        var visible = new List<NavView>
        {
            new("memory", "/memory", "记忆", "database", "工作区", null, 50, 20)
        };

        // /memory/detail/a.md 应高亮 /memory
        SidebarProjector.SelectedKeys("memory/detail/a.md", visible).Should().Equal("memory");
    }

    [Fact]
    public void SelectedKeys_Unknown_ShouldFallBackToFirstSegment()
    {
        var visible = new List<NavView>
        {
            new("memory", "/memory", "记忆", "database", "工作区", null, 50, 20)
        };

        SidebarProjector.SelectedKeys("cron-jobs/job_1", visible).Should().Equal("cron-jobs");
    }

    // ── NavKey（AppSidebar.razor:368-369）──

    [Theory]
    [InlineData("/memory", "memory")]
    [InlineData("/memory/graph", "memory-graph")]
    [InlineData("/gateway-clients", "gateway-clients")]
    [InlineData("/", "")]
    public void NavKey_ShouldMatchLegacy(string route, string expected)
        => SidebarProjector.NavKey(route).Should().Be(expected);

    // ── 图标映射 ──

    [Fact]
    public void IconResolver_ShouldBeApplied()
    {
        var projection = SidebarProjector.Project(
            [E("m", Page("p", "/a", "A", icon: "database"))],
            token => $"antd-{token}");

        projection.Visible.Single().Icon.Should().Be("antd-database");
    }
}

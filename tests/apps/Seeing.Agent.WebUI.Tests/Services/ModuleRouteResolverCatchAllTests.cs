using FluentAssertions;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Core.Ui;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>
/// 模块路由模板匹配（现由 Core 的 <see cref="UiRouteTemplate"/> 承担）。
/// <para>
/// 重点守 <c>{*name}</c> 通配段对**多级路径**的捕获：记忆详情路由
/// <c>/memory/detail/{*FilePath}</c> 的 FilePath 形如
/// <c>daily/2026-10-18/xxx.md</c>，一旦调用方把 <c>/</c> 编码成 <c>%2F</c>，
/// 整条路径会塌成单段，详情页就读不到文件。
/// </para>
/// </summary>
public class ModuleRouteResolverCatchAllTests
{
    private static bool Match(string template, string path, out IReadOnlyDictionary<string, object?> parameters)
        => UiRouteTemplate.TryMatch(template, path, out parameters, out _);

    [Fact]
    public void CatchAll_Should_Capture_Multi_Segment_Path()
    {
        Match("/memory/detail/{*FilePath}", "/memory/detail/daily/2026-10-18/a.md", out var p)
            .Should().BeTrue();

        p["FilePath"].Should().Be("daily/2026-10-18/a.md",
            "通配段必须把剩余所有段用 / 连起来还原成文件路径");
    }

    [Fact]
    public void CatchAll_Should_Capture_Single_Segment_Path()
    {
        Match("/memory/detail/{*FilePath}", "/memory/detail/a.md", out var p)
            .Should().BeTrue();

        p["FilePath"].Should().Be("a.md");
    }

    [Fact]
    public void CatchAll_With_Percent_Encoded_Slashes_Should_Expose_Encoding_To_Caller()
    {
        // 这条钉住「为什么会 404」：Uri.EscapeDataString("daily/x/a.md")
        // = "daily%2Fx%2Fa.md"，塌成单段，捕获值就是带 %2F 的字面量。
        // 解析器不做解码（也不该做——它只负责切段），所以调用方不能编码。
        var encoded = Uri.EscapeDataString("daily/2026-10-18/a.md");
        encoded.Should().Contain("%2F");

        Match("/memory/detail/{*FilePath}", $"/memory/detail/{encoded}", out var p)
            .Should().BeTrue();

        p["FilePath"].Should().Be(encoded,
            "捕获值是编码后的字面量，直接用它会读不到文件——这正是原 bug 的形态");
        p["FilePath"].Should().NotBe("daily/2026-10-18/a.md",
            "未解码就绝不等于真实路径");
    }

    [Fact]
    public void CatchAll_Should_Not_Match_When_No_Tail()
    {
        // /memory/detail 只有前缀，没有尾巴：应捕获空串（该页展示「文件不存在」而不是崩）
        Match("/memory/detail/{*FilePath}", "/memory/detail", out var p).Should().BeTrue();
        p["FilePath"].Should().Be("");
    }

    [Fact]
    public void Deeper_Specific_Route_Should_Win_Over_CatchAll()
    {
        // 确保新增路由不会把详情路由吃掉：/memory/graph 必须匹配字面量路由
        Match("/memory/graph", "/memory/graph", out _).Should().BeTrue();
        Match("/memory/detail/{*FilePath}", "/memory/graph", out _).Should().BeFalse(
            "字面量前缀不匹配时不得由通配段顶替");
    }

    /// <summary>
    /// <see cref="UiRouteTemplate.Resolve"/> 必须在多条候选中挑出字面量更多的那条，
    /// 否则 <c>/memory/graph</c> 会被 catch-all 详情路由抢走。
    /// </summary>
    [Fact]
    public void Resolve_ShouldPreferLiteralRoute_OverCatchAll()
    {
        UiContributionEntry[] entries =
        [
            new("memory", new PageContribution
            {
                Point = "shell.page", Id = "page.memory.detail",
                Route = "/memory/detail/{*FilePath}", Title = "详情", ViewKey = "page.memory.detail",
            }),
            new("memory", new PageContribution
            {
                Point = "shell.page", Id = "page.memory.graph",
                Route = "/memory/graph", Title = "知识图谱", ViewKey = "page.memory.graph",
            }),
        ];

        UiRouteTemplate.Resolve("/memory/graph", entries)!.Page.Id.Should().Be("page.memory.graph");
    }
}

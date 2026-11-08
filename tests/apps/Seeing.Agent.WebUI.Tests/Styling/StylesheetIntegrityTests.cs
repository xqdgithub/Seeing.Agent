using FluentAssertions;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Styling;

/// <summary>
/// 样式表结构守卫。
/// <para>
/// 为什么需要它：CSS 少一个大括号不会报错，浏览器会把后面的规则静默丢弃，
/// 表现为「页面突然完全没了样式」而没有任何日志。曾因为按行截断文件丢了末尾的
/// <c>}</c>，导致整个工作区页样式失效。
/// </para>
/// </summary>
public class StylesheetIntegrityTests
{
    private static readonly string SourceFilePath = CurrentFile();

    private static string CurrentFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    [Theory]
    [InlineData("workspace-page.css")]
    [InlineData("sidebar.css")]
    [InlineData("home-console.css")]
    [InlineData("design-system.css")]
    [InlineData("main-layout.css")]
    public void Braces_Should_Be_Balanced(string fileName)
    {
        var path = Path.Combine(FindRepositoryRoot(), "samples", "Seeing.Agent.WebUI", "wwwroot", "css", fileName);
        File.Exists(path).Should().BeTrue($"{fileName} 应存在于 wwwroot/css");

        var css = File.ReadAllText(path);
        var open = css.Count(c => c == '{');
        var close = css.Count(c => c == '}');

        open.Should().Be(close, $"{fileName} 的大括号必须配平，否则后续规则会被静默丢弃");
    }

    [Fact]
    public void WorkspacePage_Css_Should_Contain_Layout_And_Card_Rules()
    {
        var path = Path.Combine(
            FindRepositoryRoot(), "samples", "Seeing.Agent.WebUI", "wwwroot", "css", "workspace-page.css");
        var css = File.ReadAllText(path);

        // 这三个是工作区页可用的最低要求：整页容器、网格、卡片
        css.Should().Contain(".fullpage-layout");
        css.Should().Contain(".ws-page ");
        css.Should().Contain(".ws-page-grid");
        css.Should().Contain(".ws-page-card");
    }

    [Fact]
    public void WorkspacePage_Should_Not_Reuse_Console_Skeleton()
    {
        // .hc 是控制台的全高骨架（height:100% + overflow:hidden），套到无侧栏页上会裁掉内容
        var path = Path.Combine(
            FindRepositoryRoot(), "samples", "Seeing.Agent.WebUI", "Pages", "WorkspacesPage.razor");
        File.ReadAllText(path).Should().NotContain("class=\"hc\"");
    }

    [Fact]
    public void WorkspacePage_Should_Use_AddCard_Instead_Of_Header_Picker_Button()
    {
        var page = Path.Combine(
            FindRepositoryRoot(), "samples", "Seeing.Agent.WebUI", "Pages", "WorkspacesPage.razor");
        var css = Path.Combine(
            FindRepositoryRoot(), "samples", "Seeing.Agent.WebUI", "wwwroot", "css", "workspace-page.css");

        var markup = File.ReadAllText(page);
        markup.Should().Contain("ws-page-add", "新增工作区入口应是网格里的 + 卡片");
        markup.Should().NotContain("选择文件夹</Button>", "页头不再放「选择文件夹」按钮");

        var styles = File.ReadAllText(css);
        styles.Should().Contain(".ws-page-add-plus", "+ 卡片需要独立的加号样式");
    }

    [Fact]
    public void WorkspacePage_Should_Only_Add_Or_Remove()
    {
        var page = Path.Combine(
            FindRepositoryRoot(), "samples", "Seeing.Agent.WebUI", "Pages", "WorkspacesPage.razor");
        var markup = File.ReadAllText(page);

        // 工作区只支持「新增 / 移除」：不提供在已有卡片上换目录的入口
        markup.Should().NotContain("换目录");
        // 状态文案用「使用中」，不用口语化的「已在用」
        markup.Should().NotContain("已在用");
        markup.Should().Contain("使用中");
    }

    [Fact]
    public void Workspace_Card_Main_Should_Grow_So_Divider_Aligns()
    {
        var css = Path.Combine(
            FindRepositoryRoot(), "samples", "Seeing.Agent.WebUI", "wwwroot", "css", "workspace-page.css");
        var styles = File.ReadAllText(css);

        // .ws-page-card 是纵向 flex，foot（带分割线）跟在 main 后面。
        // main 若不占满剩余高度，内容少的卡片（如当前项：meta 行为空）主体更矮，
        // 分割线会跟着上移，同一行卡片的分割线参差不齐。
        styles.Should().Contain("flex: 1;",
            "卡片主体需要 flex:1 把 foot 推到底部，否则分割线位置随内容高度漂移");
        styles.Should().Contain(".ws-page-card-meta",
            "meta 行需要固定最小高度，保证各卡片主体内部节奏一致");
    }

    private static string FindRepositoryRoot()
    {
        // 不依赖输出目录：BaseOutputPath 可被重定向（CI、并行验证），那时向上找 slnx 会失败。
        // 用编译期源文件路径定位仓库根，永远稳定。
        return Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(SourceFilePath)!, "..", "..", "..", ".."));
    }
}

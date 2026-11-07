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

    private static string FindRepositoryRoot()
    {
        // 不依赖输出目录：BaseOutputPath 可被重定向（CI、并行验证），那时向上找 slnx 会失败。
        // 用编译期源文件路径定位仓库根，永远稳定。
        return Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(SourceFilePath)!, "..", "..", "..", ".."));
    }
}

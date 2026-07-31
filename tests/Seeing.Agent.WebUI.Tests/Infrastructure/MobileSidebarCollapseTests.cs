using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Infrastructure;

/// <summary>
/// 移动端侧边栏折叠 bug 的回归测试。
/// bug 表现：移动浏览器模式下展开左侧导航后点击折叠无效果。
/// 根因：z-index 层级反转——Sider(200) > 遮罩(180) > 汉堡(150)，
/// Sider 展开时拦截所有点击，遮罩和汉堡都无法触发折叠。
/// </summary>
public class MobileSidebarCollapseTests
{
    private static string RepoRoot => FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Seeing.Agent.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("无法定位仓库根目录（未找到 Seeing.Agent.slnx）");
    }

    private static string WebUiPath =>
        Path.Combine(RepoRoot, "samples", "Seeing.Agent.WebUI");

    private static string AppSidebarPath =>
        Path.Combine(WebUiPath, "Components", "AppSidebar.razor");

    private static string SidebarCssPath =>
        Path.Combine(WebUiPath, "wwwroot", "css", "sidebar.css");

    private static string MainLayoutCssPath =>
        Path.Combine(WebUiPath, "wwwroot", "css", "main-layout.css");

    [Fact]
    public void Sider_Collapsible_Should_Be_Disabled_In_MobileMode()
    {
        // 移动端通过 CSS class 控制侧栏显隐，不应使用 AntDesign Sider 内置折叠机制。
        // Collapsible 在移动端应关闭，避免 Sider 内部状态与 CSS 显隐冲突。
        var source = File.ReadAllText(AppSidebarPath);
        source.Should().Contain("Collapsible=\"@(!AppState.IsMobile)\"",
            "移动端模式下必须关闭 Sider 的 Collapsible，"
            + "只用 CSS sider-overlay/sider-hidden 控制显隐");
    }

    [Fact]
    public void ZIndex_Hamburger_Should_Be_Highest_Then_Sider_Then_Overlay()
    {
        // z-index 层级：汉堡(220) > Sider(200) > 遮罩(190) > 页面内容。
        // 遮罩必须在 Sider 之下，否则展开侧栏后遮罩会挡住侧栏内容导致无法操作。
        // 汉堡必须在最上层保持始终可点击。
        var sidebarCss = File.ReadAllText(SidebarCssPath);
        var mainLayoutCss = File.ReadAllText(MainLayoutCssPath);

        var hamburgerMatch = Regex.Match(sidebarCss,
            @"\.mobile-hamburger\s*\{[^}]*z-index:\s*(\d+)", RegexOptions.Singleline);
        var overlayMatch = Regex.Match(mainLayoutCss,
            @"\.sidebar-overlay-mask\s*\{[^}]*z-index:\s*(\d+)", RegexOptions.Singleline);

        hamburgerMatch.Success.Should().BeTrue();
        overlayMatch.Success.Should().BeTrue();

        var hamburgerZ = int.Parse(hamburgerMatch.Groups[1].Value);
        var overlayZ = int.Parse(overlayMatch.Groups[1].Value);
        const int siderOverlayZ = 200;

        hamburgerZ.Should().BeGreaterThan(siderOverlayZ,
            $"汉堡({hamburgerZ}) 必须在 Sider({siderOverlayZ})之上");
        overlayZ.Should().BeLessThan(siderOverlayZ,
            $"遮罩({overlayZ}) 必须在 Sider({siderOverlayZ})之下，否则遮罩挡住侧栏内容");
    }
}

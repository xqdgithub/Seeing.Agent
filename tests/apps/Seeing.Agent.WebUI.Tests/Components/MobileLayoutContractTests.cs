using FluentAssertions;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Components;

/// <summary>
/// 移动端布局契约：窄视口必须收进汉堡，审批入口不得再外挂成左侧灰条/独立 FAB。
/// </summary>
public class MobileLayoutContractTests
{
    private static readonly string SourceFilePath = CurrentFile();

    private static string CurrentFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    [Fact]
    public void Mobile_Detection_Should_Include_Narrow_Viewport()
    {
        var js = ReadWebUiFile("wwwroot", "js", "app.js");

        js.Should().Contain("max-width: 768px",
            "窄视口（DevTools Responsive / 桌面缩窗）必须走汉堡布局，否则左侧灰侧栏占宽");
        js.Should().Contain("narrowViewport",
            "窄视口判定应有具名变量，避免以后被误删");
        js.Should().Contain("subscribeMobileLayoutChanged",
            "视口跨过 768 时需通知 .NET，否则首屏后缩窗不会切汉堡");
    }

    [Fact]
    public void MainLayout_Should_Subscribe_Mobile_Layout_Changes()
    {
        var razor = ReadWebUiFile("Shared", "MainLayout.razor");

        razor.Should().Contain("subscribeMobileLayoutChanged",
            "首屏后应订阅窄视口变化");
        razor.Should().Contain("OnMobileLayoutChanged",
            "JS 回调入口必须存在");
        razor.Should().Contain("main-layout-content--mobile",
            "移动端内容区必须去掉侧栏左边距，会话才能占满窗口");
    }

    [Fact]
    public void Mobile_Should_Not_Host_Separate_Permission_Fab()
    {
        var razor = ReadWebUiFile("Shared", "MainLayout.razor");

        // 审批中心已在 AppSidebar 汉堡菜单内（IconTemplate + Badge）。
        // 外挂 FAB 会让人以为审批没进汉堡，且与「左侧灰菜单条」症状叠在一起难排查。
        razor.Should().NotContain("IconType.Outline.SafetyCertificate",
            "MainLayout 不应再挂独立审批悬浮按钮");
        razor.Should().NotContain("OpenPermissionCenter",
            "打开审批应只走侧栏菜单 / UiOverlayService，不再经 MainLayout FAB");
    }

    [Fact]
    public void AppSidebar_Should_Hide_When_Mobile_Closed()
    {
        var razor = ReadWebUiFile("Components", "AppSidebar.razor");
        var css = ReadWebUiFile("wwwroot", "css", "sidebar.css");

        razor.Should().Contain("sider-hidden",
            "移动端关闭时应给侧栏 sider-hidden");
        css.Should().Contain(".app-sider.sider-hidden",
            "sider-hidden 必须真正隐藏，否则灰条仍占视觉");
        css.Should().Contain("display: none !important",
            "隐藏须压过 AntDesign 内联宽度");
    }

    private static string ReadWebUiFile(params string[] relativeSegments)
    {
        var parts = new List<string> { FindRepositoryRoot(), "samples", "Seeing.Agent.WebUI" };
        parts.AddRange(relativeSegments);

        var path = Path.Combine(parts.ToArray());
        File.Exists(path).Should().BeTrue($"应找到源文件 {path}");

        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(SourceFilePath)!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Seeing.Agent.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("未找到仓库根目录（Seeing.Agent.slnx）");
    }
}

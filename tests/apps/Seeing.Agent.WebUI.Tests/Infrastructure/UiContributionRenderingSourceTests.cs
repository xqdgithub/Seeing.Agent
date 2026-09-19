using System.Text.RegularExpressions;
using FluentAssertions;

namespace Seeing.Agent.WebUI.Tests.Infrastructure;

/// <summary>
/// AppSidebar / Settings 从 IUiContributionRegistry 渲染的源码扫描不变量。
/// </summary>
public class UiContributionRenderingSourceTests
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

    private static string AppSidebarPath =>
        Path.Combine(RepoRoot, "samples", "Seeing.Agent.WebUI", "Components", "AppSidebar.razor");

    private static string SettingsPath =>
        Path.Combine(RepoRoot, "samples", "Seeing.Agent.WebUI", "Pages", "Settings.razor");

    [Fact]
    public void AppSidebar_ShouldNotHardcodeModuleMenuItems()
    {
        var source = File.ReadAllText(AppSidebarPath);

        source.Should().Contain("Registry.NavItems", "侧栏必须读 IUiContributionRegistry.NavItems");
        source.Should().Contain("UiContributionVisibility.FilterSidebarNav");

        var hardcoded = new[]
        {
            "NavigateTo(\"/cron-jobs\")",
            "NavigateTo(\"/heartbeat\")",
            "NavigateTo(\"/skills\")",
            "NavigateTo(\"/tools\")",
            "NavigateTo(\"/mcp\")",
            "NavigateTo(\"/acp\")",
            "NavigateTo(\"/memory\")",
            "NavigateTo(\"/gateway\")",
            "NavigateTo(\"/agents\")",
            "Key=\"cron-jobs\"",
            "Key=\"skills\"",
            "Key=\"mcp\"",
        };

        foreach (var needle in hardcoded)
        {
            source.Should().NotContain(needle,
                $"AppSidebar 不得硬编码模块菜单项: {needle}");
        }

        // 除聊天/审批中心壳入口外，菜单项应来自 @foreach 注册表（未分组 + 分组两套模板）
        Regex.Matches(source, @"<MenuItem\b").Count.Should().Be(4,
            "仅允许：1 个聊天壳 MenuItem + 1 个审批中心壳 MenuItem + 1 个未分组 @foreach + 1 个分组 @foreach 模板 MenuItem");
    }

    [Fact]
    public void Settings_ShouldRenderCardsFromRegistry()
    {
        var source = File.ReadAllText(SettingsPath);

        source.Should().Contain("Registry.SettingsCards", "设置页必须读 SettingsCards");
        source.Should().Contain("UiContributionVisibility.FilterSettingsCards");
        source.Should().Contain("DynamicComponent");
        source.Should().NotContain("SchedulerStatusPanel",
            "调度器设置应经 SettingsCard 贡献挂载，不得在 Settings 硬编码");
        source.Should().NotContain("TabPane Key=\"scheduler\"",
            "不得硬编码 scheduler TabPane");
    }
}

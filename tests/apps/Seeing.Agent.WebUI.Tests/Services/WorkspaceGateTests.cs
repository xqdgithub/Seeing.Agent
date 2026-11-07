using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class WorkspaceGateTests
{
    [Theory]
    [InlineData("")]
    [InlineData("sessions")]
    [InlineData("settings")]
    [InlineData("session/abc")]
    public void Unresolved_ShouldForceHome_ForEveryPathButHome(string path)
    {
        WorkspaceGate.ShouldForceWorkspaceHome(isResolved: false, relativePath: path)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("workspaces")]
    [InlineData("workspaces/")]
    [InlineData("workspaces?x=1")]
    public void Unresolved_ShouldNotRedirectFromHome(string path) =>
        WorkspaceGate.ShouldForceWorkspaceHome(false, path).Should().BeFalse();

    [Fact]
    public void Resolved_ShouldNeverForceHome() =>
        WorkspaceGate.ShouldForceWorkspaceHome(true, "").Should().BeFalse();

    [Fact]
    public void ModuleRouter_ShouldNot_NavigateTo_WhileUnresolved()
    {
        // 组件初始化（含静态预渲染）阶段调用 NavigationManager.NavigateTo 会抛
        // NavigationException，把整个 circuit 打掉。未选定时必须原地渲染选择页。
        var routerPath = Path.Combine(
            FindRepositoryRoot(), "samples", "Seeing.Agent.WebUI", "Components", "ModuleRouter.razor");
        var source = File.ReadAllText(routerPath);

        source.Should().NotContain("Navigation.NavigateTo(",
            "未选定工作区时不能导航，否则初始化阶段抛 NavigationException");
        source.Should().Contain("<WorkspacesPage",
            "未选定时应原地渲染工作区选择页");
    }

    private static string FindRepositoryRoot()
    {
        // 用编译期源文件路径定位仓库根：BaseOutputPath 可被重定向时输出目录不在仓库树下
        return Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!, "..", "..", "..", ".."));
    }

    private static string CurrentFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}

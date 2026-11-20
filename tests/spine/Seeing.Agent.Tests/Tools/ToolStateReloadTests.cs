using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Tools;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

/// <summary>
/// 工作区切换后 tool-state 必须从零重建：旧工作区的项目级禁用集合要整体消失，
/// 用户级禁用与工作区无关、必须保留。
/// </summary>
public class ToolStateReloadTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "tool-state-reload-" + Guid.NewGuid().ToString("N"));

    private string UserSeeing => Path.Combine(_root, "user", ".seeing");

    private string ProjectSeeing(string workspace) => Path.Combine(_root, workspace, ".seeing");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 清理失败忽略（文件锁等）
        }
    }

    [Fact]
    public async Task 切换工作区_项目级禁用集合重建_用户级保留()
    {
        // Arrange: 用户级禁用 user_tool；工作区 A 禁用 a_tool；工作区 B 禁用 b_tool
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(UserSeeing);
        Directory.CreateDirectory(ProjectSeeing("ws-a"));
        Directory.CreateDirectory(ProjectSeeing("ws-b"));

        await File.WriteAllTextAsync(
            Path.Combine(UserSeeing, "tool-state.json"),
            """{"disabledTools":["user_tool"]}""",
            ct);
        await File.WriteAllTextAsync(
            Path.Combine(ProjectSeeing("ws-a"), "tool-state.json"),
            """{"disabledTools":["a_tool"]}""",
            ct);
        await File.WriteAllTextAsync(
            Path.Combine(ProjectSeeing("ws-b"), "tool-state.json"),
            """{"disabledTools":["b_tool"]}""",
            ct);

        var currentProjectSeeing = ProjectSeeing("ws-a");
        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(w => w.UserSeeingDirectory).Returns(UserSeeing);
        workspace.Setup(w => w.ProjectSeeingDirectory).Returns(() => currentProjectSeeing);

        var manager = new ToolManager(
            NullLogger<ToolManager>.Instance,
            new HookManager(NullLogger<HookManager>.Instance),
            workspace: workspace.Object);

        await manager.LoadToolStateAsync(ct);

        manager.IsToolEnabled("a_tool").Should().BeFalse();
        manager.IsToolEnabled("b_tool").Should().BeTrue();
        manager.IsToolEnabled("user_tool").Should().BeFalse();

        // Act: 切到工作区 B 后由重载处理器重载
        currentProjectSeeing = ProjectSeeing("ws-b");
        var handler = new ToolStateReloadHandler(manager);
        await handler.ReloadAsync(
            new WorkspaceChange
            {
                OldWorkspace = Path.Combine(_root, "ws-a"),
                NewWorkspace = Path.Combine(_root, "ws-b")
            },
            ct);

        // Assert
        manager.IsToolEnabled("a_tool").Should().BeTrue("旧工作区的项目级禁用必须解禁");
        manager.IsToolEnabled("b_tool").Should().BeFalse("新工作区的项目级禁用必须生效");
        manager.IsToolEnabled("user_tool").Should().BeFalse("用户级禁用与工作区无关");
    }

    [Fact]
    public void 处理器声明tool_state组件与工作区变更订阅()
    {
        var manager = new ToolManager(
            NullLogger<ToolManager>.Instance,
            new HookManager(NullLogger<HookManager>.Instance));

        var handler = new ToolStateReloadHandler(manager);

        handler.ComponentId.Should().Be("tool-state");
        handler.ChangeTypes.Should().Contain(typeof(WorkspaceChange));
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Tools;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

/// <summary>
/// ToolManager 生命周期回归：Singleton 由 DI 容器托管，宿主关闭时须释放写盘门（SemaphoreSlim）。
/// </summary>
public sealed class ToolManagerDisposeTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "tool-manager-dispose-" + Guid.NewGuid().ToString("N"));

    private ToolManager CreateManager()
    {
        var userSeeing = Path.Combine(_tempDirectory, "user", ".seeing");
        var projectSeeing = Path.Combine(_tempDirectory, "project", ".seeing");
        Directory.CreateDirectory(userSeeing);
        Directory.CreateDirectory(projectSeeing);

        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(w => w.UserSeeingDirectory).Returns(userSeeing);
        workspace.Setup(w => w.ProjectSeeingDirectory).Returns(projectSeeing);

        return new ToolManager(
            NullLogger<ToolManager>.Instance,
            new HookManager(NullLogger<HookManager>.Instance),
            workspace: workspace.Object);
    }

    [Fact]
    public async Task Dispose_ThenSetToolEnabled_ThrowsObjectDisposed()
    {
        var manager = CreateManager();

        manager.Dispose();

        var act = async () => await manager.SetToolEnabledAsync("tool", enabled: false);

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var manager = CreateManager();

        var act = () =>
        {
            manager.Dispose();
            manager.Dispose();
        };

        act.Should().NotThrow();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }
}

using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.WebUI.Files.Services;

namespace Seeing.Agent.WebUI.Files.Tests;

public class FileTreeRootsTests
{
    private static IFileWorkspace Workspace(string rootId, string rootPath, string label, bool exists)
    {
        var ws = new Mock<IFileWorkspace>();
        ws.SetupGet(w => w.RootId).Returns(rootId);
        ws.SetupGet(w => w.RootPath).Returns(rootPath);
        ws.SetupGet(w => w.Root).Returns(new FileWorkspaceRoot(
            rootId, rootPath, label, FileWorkspaceRootKind.Project, exists));
        return ws.Object;
    }

    [Fact]
    public void Badge_Project_ShouldBeProject()
        => FileTreeRoots.Badge(FileWorkspaceRootKind.Project).Should().Be("项目");

    [Fact]
    public void Badge_Collaboration_ShouldBeCollab()
        => FileTreeRoots.Badge(FileWorkspaceRootKind.Collaboration).Should().Be("协同");

    [Fact]
    public void DisplayName_MissingRoot_ShouldAppendUnavailable()
    {
        var root = new FileWorkspaceRoot("r1", @"D:\data", "数据", FileWorkspaceRootKind.Collaboration, false);

        FileTreeRoots.DisplayName(root).Should().Contain("不可用");
    }

    [Fact]
    public void DisplayName_NoLabel_ShouldUseDirectoryName()
    {
        var root = new FileWorkspaceRoot(
            "r1", Path.Combine(Path.GetTempPath(), "my-report"), "", FileWorkspaceRootKind.Collaboration, true);

        FileTreeRoots.DisplayName(root).Should().Be("my-report");
    }

    [Fact]
    public void Entry_ShouldUseRootIdAndEmptyRelativePath()
    {
        var ws = Workspace("r1", @"D:\data", "数据", true);

        var entry = FileTreeRoots.Entry(ws);

        entry.Id.Should().Be(new FileResourceId("r1", ""));
        entry.IsDirectory.Should().BeTrue();
        entry.Name.Should().Be("数据");
    }
}

using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.WebUI.Files.Services;

namespace Seeing.Agent.WebUI.Files.Tests;

public class FileWorkspaceSetTests
{
    private static IFileWorkspace Root(string rootId, string rootPath)
    {
        var ws = new Mock<IFileWorkspace>();
        ws.SetupGet(w => w.RootId).Returns(rootId);
        ws.SetupGet(w => w.RootPath).Returns(rootPath);
        return ws.Object;
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fws_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Find_ForeignRootId_ShouldReturnNull()
    {
        var dir = NewDir();
        var set = new FileWorkspaceSet([Root("r1", dir)]);

        set.Find(new FileResourceId("r3", "a.txt")).Should().BeNull();
    }

    [Fact]
    public void Find_KnownRootId_ShouldReturnWorkspace()
    {
        var dir = NewDir();
        var set = new FileWorkspaceSet([Root("r1", dir)]);

        set.Find(new FileResourceId("r1", "a.txt"))!.RootId.Should().Be("r1");
    }

    [Fact]
    public void FindByPath_LongestPrefixWins()
    {
        var parent = NewDir();
        var shortRoot = Path.Combine(parent, "a");
        var longRoot = Path.Combine(parent, "ab");
        var set = new FileWorkspaceSet([Root("r1", shortRoot), Root("r2", longRoot)]);

        set.FindByPath(Path.Combine(longRoot, "x.txt"))!.RootId.Should().Be("r2");
        set.FindByPath(Path.Combine(shortRoot, "x.txt"))!.RootId.Should().Be("r1");
    }

    [Fact]
    public void FindByPath_RelativePath_ShouldReturnNull()
    {
        var set = new FileWorkspaceSet([Root("r1", NewDir())]);

        set.FindByPath("a.txt").Should().BeNull();
    }

    [Fact]
    public void FindByPath_OutsideAllRoots_ShouldReturnNull()
    {
        var set = new FileWorkspaceSet([Root("r1", NewDir())]);

        set.FindByPath(Path.Combine(NewDir(), "a.txt")).Should().BeNull();
    }
}

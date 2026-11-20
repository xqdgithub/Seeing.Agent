using FluentAssertions;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.Files.Watching;
using Seeing.IO.Local;
using Xunit;

namespace Seeing.Agent.Files.Tests;

/// <summary>
/// 多根路由的最高风险回归：错 RootId / 越界相对路径绝不能被解析到主根同名文件。
/// </summary>
public class FileWorkspaceRoutingTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fwr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static FileWorkspace NewWorkspace(string root)
        => new("s1", root, new LocalFileSystem(), new AllowAllGate(), new FileWatcherPool(), new ProcessPathWriteLock());

    [Fact]
    public void GetAbsolutePath_ForeignRootId_ShouldThrow()
    {
        var root = NewDir();
        var ws = NewWorkspace(root);

        var act = () => ws.GetAbsolutePath(new FileResourceId("other-root", "x.txt"));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GetAbsolutePath_EscapingRelativePath_ShouldThrow()
    {
        var root = NewDir();
        var ws = NewWorkspace(root);

        var act = () => ws.GetAbsolutePath(new FileResourceId(ws.RootId, "../x.txt"));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GetResourceId_PathOutsideRoot_ShouldThrow()
    {
        var root = NewDir();
        var outside = NewDir();
        var ws = NewWorkspace(root);

        var act = () => ws.GetResourceId(Path.Combine(outside, "a.txt"));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GetResourceId_RelativePath_ShouldResolveAgainstRoot_NotProcessCwd()
    {
        var root = NewDir();
        var ws = NewWorkspace(root);

        var id = ws.GetResourceId("sub/a.txt");

        id.RootId.Should().Be(ws.RootId);
        id.RelativePath.Should().Be("sub/a.txt");
        ws.GetAbsolutePath(id).Should().Be(Path.GetFullPath(Path.Combine(root, "sub", "a.txt")));
    }

    [Fact]
    public void Root_ShouldCarryKindAndLabel()
    {
        var root = NewDir();
        var ws = NewWorkspace(root);

        ws.Root.Kind.Should().Be(FileWorkspaceRootKind.Project);
        ws.Root.RootPath.Should().Be(Path.GetFullPath(root));
        ws.Root.Exists.Should().BeTrue();
    }
}

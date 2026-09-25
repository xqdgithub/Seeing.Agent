using FluentAssertions;
using Seeing.Agent.Acp.Filesystem;
using Xunit;

namespace Seeing.Agent.Acp.Tests;

public class AcpFileSystemBridgeTests
{
    [Fact]
    public void TryResolvePath_RootItself_ShouldAllow()
    {
        var root = NewRoot();

        var allowed = AcpFileSystemBridge.TryResolvePath(root, root, out var fullPath);

        allowed.Should().BeTrue();
        fullPath.Should().Be(Path.GetFullPath(root));
    }

    [Fact]
    public void TryResolvePath_ChildOfRoot_ShouldAllow()
    {
        var root = NewRoot();

        var allowed = AcpFileSystemBridge.TryResolvePath(Path.Combine(root, "x.txt"), root, out var fullPath);

        allowed.Should().BeTrue();
        fullPath.Should().Be(Path.GetFullPath(Path.Combine(root, "x.txt")));
    }

    [Fact]
    public void TryResolvePath_RelativeChildOfRoot_ShouldAllow()
    {
        var root = NewRoot();

        var allowed = AcpFileSystemBridge.TryResolvePath("sub\\x.txt", root, out _);

        allowed.Should().BeTrue();
    }

    [Fact]
    public void TryResolvePath_SiblingWithSharedPrefix_ShouldReject()
    {
        // 兄弟目录 app 与 apple 共享字符串前缀，必须按分隔符边界拒绝
        var root = NewRoot();
        var sibling = Path.Combine(Path.GetDirectoryName(root)!, Path.GetFileName(root) + "le", "x.txt");

        var allowed = AcpFileSystemBridge.TryResolvePath(sibling, root, out _);

        allowed.Should().BeFalse();
    }

    [Fact]
    public void TryResolvePath_ParentTraversalIntoPrefixSibling_ShouldReject()
    {
        var root = NewRoot();
        var escaped = Path.Combine(root, "..", Path.GetFileName(root) + "le", "x.txt");

        var allowed = AcpFileSystemBridge.TryResolvePath(escaped, root, out _);

        allowed.Should().BeFalse();
    }

    private static string NewRoot()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"acp-ws-{Guid.NewGuid():N}");
        return Path.Combine(ws, "app");
    }
}

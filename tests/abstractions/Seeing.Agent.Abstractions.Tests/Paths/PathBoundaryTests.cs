using FluentAssertions;
using Seeing.Agent.Paths;
using Xunit;

namespace Seeing.Agent.Abstractions.Tests.Paths;

/// <summary>
/// <see cref="PathBoundary"/> 的边界语义锁定。
/// <para>
/// <b>为什么需要它：</b>本类是「路径是否在根内」的全项目唯一实现，替换了此前 5 份各自演进的副本
/// （Core 的 <c>PathSafety</c>、Tools.FileSystem 的 <c>FileSystemHelper</c>、Skills 两份内联前缀比较、
/// ACP 文件桥的字符串前缀比较）。其中 ACP 与 Skills 的副本<b>不做符号链接真实路径解析</b>，
/// 使硬边界（<c>Workspace.RestrictToWorkspace</c>）在那些路径上形同不存在。
/// 这些都是安全边界，必须由测试锁住，而不是靠代码审查。
/// </para>
/// </summary>
public class PathBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "seeing-pathboundary-" + Guid.NewGuid().ToString("N"));

    public PathBoundaryTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "inside"));
        Directory.CreateDirectory(Path.Combine(_root, "inside", "nested"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void IsWithin_SameDirectory_ShouldBeTrue()
        => PathBoundary.IsWithin(_root, _root).Should().BeTrue();

    [Fact]
    public void IsWithin_ChildDirectory_ShouldBeTrue()
        => PathBoundary.IsWithin(Path.Combine(_root, "inside", "nested"), _root).Should().BeTrue();

    [Fact]
    public void IsWithin_ParentDirectory_ShouldBeFalse()
        => PathBoundary.IsWithin(Path.GetTempPath(), _root).Should().BeFalse();

    [Fact]
    public void IsWithin_SiblingWithSharedPrefix_ShouldBeFalse()
    {
        // 回归：E:\ws\apple 不得命中 root=E:\ws\app（必须带分隔符边界）
        var sibling = _root + "-evil";
        Directory.CreateDirectory(sibling);
        try
        {
            PathBoundary.IsWithin(sibling, _root).Should().BeFalse(
                "前缀相同但不是子路径：分隔符边界必须生效");
        }
        finally
        {
            try { Directory.Delete(sibling, recursive: true); } catch { }
        }
    }

    [Fact]
    public void IsWithin_DotDotEscape_ShouldBeFalse()
        => PathBoundary.IsWithin(Path.Combine(_root, "..", "outside.txt"), _root).Should().BeFalse();

    [Fact]
    public void IsWithin_EmptyOrWhitespace_ShouldBeFalse()
    {
        PathBoundary.IsWithin("", _root).Should().BeFalse();
        PathBoundary.IsWithin(_root, "").Should().BeFalse();
        PathBoundary.IsWithin("   ", _root).Should().BeFalse();
    }

    [Fact]
    public void IsStrictlyWithin_SameDirectory_ShouldBeFalse()
    {
        // 严格子路径：相等不算「之内」（供 Skills 的 IsUnder 使用）
        PathBoundary.IsStrictlyWithin(_root, _root).Should().BeFalse();
        PathBoundary.IsStrictlyWithin(Path.Combine(_root, "inside"), _root).Should().BeTrue();
    }

    [Fact]
    public void AreSame_TrailingSeparatorInsensitive_ShouldBeTrue()
        => PathBoundary.AreSame(_root + Path.DirectorySeparatorChar, _root).Should().BeTrue();

    [Fact]
    public void IsWithin_SymlinkPointingOutside_ShouldBeFalse()
    {
        // 这是本类存在的核心原因：不做 realpath 的副本会在此判为「在根内」。
        var linkPath = Path.Combine(_root, "escape-link");
        var outside = Path.Combine(Path.GetTempPath(), "seeing-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);

        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkPath, outside);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                // 无符号链接权限（如 Windows 未开开发者模式）：跳过该断言而非误报通过
                return;
            }

            PathBoundary.IsWithin(linkPath, _root).Should().BeFalse(
                "符号链接指向根外时必须判为越界（realpath 解析是安全前提）");
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PathComparison_ShouldMatchPlatformSemantics()
    {
        var expected = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        PathBoundary.PathComparison.Should().Be(expected);
    }
}

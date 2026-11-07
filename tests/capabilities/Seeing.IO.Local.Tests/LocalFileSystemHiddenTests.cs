using FluentAssertions;
using Seeing.IO.Local;
using Xunit;

namespace Seeing.IO.Local.Tests;

/// <summary>
/// <see cref="LocalFileSystem.IsHidden"/> 语义测试。
/// <para>
/// 必须与 ripgrep 一致：所有平台判点前缀；Windows 额外判 <see cref="FileAttributes.Hidden"/>。
/// 实测 rg 默认同时跳过这两类项，<c>--hidden</c> 两者都放开。
/// </para>
/// </summary>
public class LocalFileSystemHiddenTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileSystem _fs = new();

    public LocalFileSystemHiddenTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "seeing-hidden-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                // 清掉 Hidden 属性，否则递归删除可能失败
                foreach (var file in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);

                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清理由 OS 兜底
        }
    }

    private string Create(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void IsHidden_DotPrefixedFile_ShouldBeTrue()
    {
        _fs.IsHidden(Create(".env")).Should().BeTrue();
    }

    [Fact]
    public void IsHidden_DotPrefixedDirectory_ShouldBeTrue()
    {
        var dir = Path.Combine(_root, ".github");
        Directory.CreateDirectory(dir);

        _fs.IsHidden(dir).Should().BeTrue();
    }

    [Fact]
    public void IsHidden_PlainFile_ShouldBeFalse()
    {
        _fs.IsHidden(Create("a.md")).Should().BeFalse();
    }

    [Fact]
    public void IsHidden_WindowsHiddenAttribute_ShouldBeTrue()
    {
        // rg 在 Windows 上同样跳过 Hidden 属性项，托管侧必须一致
        var file = Create("winhidden.md");
        File.SetAttributes(file, FileAttributes.Hidden);

        if (!OperatingSystem.IsWindows())
        {
            // 非 Windows 平台无此概念，点前缀判定已覆盖
            _fs.IsHidden(file).Should().BeFalse();
            return;
        }

        _fs.IsHidden(file).Should().BeTrue();
    }

    [Fact]
    public void IsHidden_MissingPath_ShouldBeFalse()
    {
        // 缺失路径按非隐藏处理（避免误伤）
        _fs.IsHidden(Path.Combine(_root, "does-not-exist")).Should().BeFalse();
    }

    [Fact]
    public void IsHidden_TrailingSeparator_ShouldStillDetectDotPrefix()
    {
        var dir = Path.Combine(_root, ".git");
        Directory.CreateDirectory(dir);

        _fs.IsHidden(dir + Path.DirectorySeparatorChar).Should().BeTrue();
    }
}

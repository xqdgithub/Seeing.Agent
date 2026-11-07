using FluentAssertions;
using Seeing.Agent.Tools.FileSystem;
using Seeing.IO.Local;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

/// <summary>
/// 托管回退（rg 不可用）的 happy-path 测试。
/// <para>
/// 此前该路径只有取消测试、没有任何"能命中"的断言，因此 <c>**</c> 转换错误与
/// "用绝对路径撞锚定正则"两个致命缺陷可以长期存在而套件全绿。
/// </para>
/// <para>
/// 用真实临时目录（期间 IO）：文件创建后立即可枚举，无固定时长等待。
/// </para>
/// </summary>
public class ManagedGlobGrepTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileSystem _fs = new();

    public ManagedGlobGrepTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "seeing-glob-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理由 OS 兜底
        }
    }

    private string Write(string relativePath, string content = "x")
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Fact]
    public void GlobSearch_DoubleStar_ShouldMatchNestedAndRootFiles()
    {
        var root = Write("a.md");
        var nested = Write("src/deep/c.md");

        var files = FileSystemHelper.GlobSearch(_fs, _root, "**/*.md");

        files.Should().HaveCount(2);
        files.Should().Contain(root);
        files.Should().Contain(nested);
    }

    [Fact]
    public void GlobSearch_BareExtension_ShouldMatchAnyDepth()
    {
        // 不含 / 的模式按 basename 规则，必须与 **/*.md 等价（rg 行为）
        var root = Write("a.md");
        var nested = Write("src/deep/c.md");

        var files = FileSystemHelper.GlobSearch(_fs, _root, "*.md");

        files.Should().HaveCount(2);
        files.Should().Contain(root);
        files.Should().Contain(nested);
    }

    [Fact]
    public void GlobSearch_ChineseName_ShouldMatch()
    {
        // 回归用户实际场景：招考简章.zip / research/招考笔记.md
        var zip = Write("招考简章.zip");
        var note = Write("research/招考笔记.md");

        var all = FileSystemHelper.GlobSearch(_fs, _root, "*招考*");
        all.Should().HaveCount(2);
        all.Should().Contain(zip);
        all.Should().Contain(note);

        FileSystemHelper.GlobSearch(_fs, _root, "**/*招考*")
            .Should().HaveCount(2);
    }

    [Fact]
    public void GlobSearch_BraceExpansion_ShouldMatchBothExtensions()
    {
        var ts = Write("a.ts");
        var tsx = Write("b.tsx");
        Write("c.js");

        var files = FileSystemHelper.GlobSearch(_fs, _root, "*.{ts,tsx}");

        files.Should().HaveCount(2);
        files.Should().Contain(ts);
        files.Should().Contain(tsx);
    }

    [Fact]
    public void GlobSearch_SubdirectoryPattern_ShouldOnlyMatchThatPath()
    {
        var hit = Write("src/a.md");
        Write("test/b.md");

        FileSystemHelper.GlobSearch(_fs, _root, "src/*.md").Should().Equal(hit);
    }

    [Fact]
    public void GlobSearch_HiddenFileAndDirectory_ShouldBeSkipped()
    {
        // 对齐 rg 默认：不输出隐藏项
        Write(".hidden.md");
        Write(".seeing/notes.md");
        Write(".github/workflows/ci.yml");
        var visible = Write("a.md");

        FileSystemHelper.GlobSearch(_fs, _root, "*.md").Should().Equal(visible);
    }

    [Fact]
    public void GlobSearch_IncludeHidden_ShouldReturnHiddenItems()
    {
        // 对齐 rg --hidden：开启后隐藏项全部可见
        var hiddenFile = Write(".hidden.md");
        var hiddenDir = Write(".github/workflows/ci.yml");
        var visible = Write("a.md");

        var files = FileSystemHelper.GlobSearch(_fs, _root, "*.md", includeHidden: true);

        files.Should().HaveCount(2);
        files.Should().Contain(hiddenFile);
        files.Should().Contain(visible);
        // 非 .md 的隐藏文件不在结果内（模式过滤仍然生效）
        files.Should().NotContain(hiddenDir);
    }

    [Fact]
    public void GlobSearch_IncludeHidden_ShouldNotExcludeGitDirectory()
    {
        // 对齐 rg --hidden 的原始语义：不额外排除 .git（实测 rg --hidden 会列出 .git 内容）
        var gitConfig = Write(".git/config");
        Write(".git/objects/abc");
        Write("a.md");

        var files = FileSystemHelper.GlobSearch(_fs, _root, "*", includeHidden: true);

        files.Should().Contain(gitConfig);
    }

    [Fact]
    public void GrepSearch_HiddenFiles_ShouldBeSkippedByDefault()
    {
        Write(".env", "needle\n");
        Write(".github/ci.yml", "needle\n");
        var visible = Write("a.md", "needle\n");

        var matches = FileSystemHelper.GrepSearch(_fs, _root, "needle");

        matches.Should().ContainSingle();
        matches[0].Path.Should().Be(visible);
    }

    [Fact]
    public void GrepSearch_IncludeHidden_ShouldSearchHiddenFiles()
    {
        var env = Write(".env", "needle\n");
        var visible = Write("a.md", "needle\n");

        var matches = FileSystemHelper.GrepSearch(_fs, _root, "needle", includeHidden: true);

        matches.Select(m => m.Path).Should().BeEquivalentTo([env, visible]);
    }

    [Fact]
    public void GlobSearch_ShouldRespectLimit()
    {
        for (var i = 0; i < 5; i++)
            Write($"f{i}.md");

        FileSystemHelper.GlobSearch(_fs, _root, "*.md", limit: 2).Should().HaveCount(2);
    }

    [Fact]
    public void GrepSearch_WithIncludeGlob_ShouldFilterByFileName()
    {
        var cs = Write("a.cs", "needle\n");
        Write("b.txt", "needle\n");

        var matches = FileSystemHelper.GrepSearch(_fs, _root, "needle", includePattern: "*.cs");

        matches.Should().ContainSingle();
        matches[0].Path.Should().Be(cs);
        matches[0].LineNum.Should().Be(1);
    }

    [Fact]
    public void GrepSearch_WithoutInclude_ShouldSearchAllTextFiles()
    {
        var a = Write("a.md", "needle\n");
        var b = Write("sub/b.md", "other\nneedle\n");

        var matches = FileSystemHelper.GrepSearch(_fs, _root, "needle");

        matches.Should().HaveCount(2);
        matches.Select(m => m.Path).Should().BeEquivalentTo([a, b]);
    }
    [Fact]
    public void GrepSearch_BinaryExtension_ShouldBeSkipped()
    {
        Write("a.zip", "needle\n");

        FileSystemHelper.GrepSearch(_fs, _root, "needle").Should().BeEmpty();
    }

    [Fact]
    public void GrepSearch_NoMatch_ShouldReturnEmpty()
    {
        Write("a.md", "something else\n");

        FileSystemHelper.GrepSearch(_fs, _root, "needle").Should().BeEmpty();
    }
}

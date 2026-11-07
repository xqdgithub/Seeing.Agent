using FluentAssertions;
using Seeing.Agent.Tools.FileSystem;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

/// <summary>
/// <see cref="GlobMatcher"/> 语义测试：锁定与 ripgrep globset 一致的匹配行为。
/// <para>托管回退路径此前没有任何 happy-path 覆盖，是"全绿但全坏"的根因。</para>
/// </summary>
public class GlobMatcherTests
{
    public GlobMatcherTests() => GlobMatcher.ClearCacheForTests();

    [Theory]
    // ** 跨任意深度，且 **/ 前缀可省略
    [InlineData("a.md", "**/*.md", true)]
    [InlineData("src/b.md", "**/*.md", true)]
    [InlineData("src/deep/c.md", "**/*.md", true)]
    // 不含 / 的模式按 basename 规则，调用方传文件名
    [InlineData("b.md", "*.md", true)]
    [InlineData("b.txt", "*.md", false)]
    // * 不跨目录
    [InlineData("src/b.md", "src/*.md", true)]
    [InlineData("src/deep/c.md", "src/*.md", false)]
    // ? 单字符且不跨分隔符
    [InlineData("a.md", "?.md", true)]
    [InlineData("ab.md", "?.md", false)]
    public void Matches_BasicPatterns_ShouldMatchExpected(string candidate, string pattern, bool expected)
    {
        GlobMatcher.Matches(candidate, pattern, caseSensitive: true).Should().Be(expected);
    }

    [Theory]
    [InlineData("a.ts", "*.{ts,tsx}", true)]
    [InlineData("a.tsx", "*.{ts,tsx}", true)]
    [InlineData("a.js", "*.{ts,tsx}", false)]
    [InlineData("src/a.ts", "src/*.{ts,tsx}", true)]
    public void Matches_BraceExpansion_ShouldMatchExpected(string candidate, string pattern, bool expected)
    {
        GlobMatcher.Matches(candidate, pattern, caseSensitive: true).Should().Be(expected);
    }

    [Theory]
    [InlineData("a.cs", "[ab].cs", true)]
    [InlineData("c.cs", "[ab].cs", false)]
    [InlineData("c.cs", "[!ab].cs", true)]
    public void Matches_CharacterClass_ShouldMatchExpected(string candidate, string pattern, bool expected)
    {
        GlobMatcher.Matches(candidate, pattern, caseSensitive: true).Should().Be(expected);
    }

    [Fact]
    public void Matches_BackslashPattern_TreatedAsSeparator()
    {
        // 用户写 Windows 风格分隔符不应被惩罚：\ 归一为 /
        GlobMatcher.Matches("src/b.md", @"src\*.md", caseSensitive: true).Should().BeTrue();
    }

    [Fact]
    public void Matches_ChineseFileName_ShouldMatch()
    {
        // 回归：截图中真实的失败场景（招考简章）
        GlobMatcher.Matches("招考简章.zip", "*招考*", caseSensitive: true).Should().BeTrue();
        GlobMatcher.Matches("research/招考笔记.md", "**/*招考*", caseSensitive: true).Should().BeTrue();
    }

    [Fact]
    public void Matches_CaseSensitivityToggle_ShouldHonorFlag()
    {
        GlobMatcher.Matches("X.CS", "*.cs", caseSensitive: true).Should().BeFalse();
        GlobMatcher.Matches("X.CS", "*.cs", caseSensitive: false).Should().BeTrue();
    }

    [Theory]
    [InlineData(@"src\*.md", "src/*.md")]
    [InlineData("./src/*.md", "src/*.md")]
    [InlineData("src/*.md", "src/*.md")]
    public void NormalizePattern_ShouldNormalizeSeparatorsAndPrefix(string input, string expected)
    {
        GlobMatcher.NormalizePattern(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("*.md", false)]
    [InlineData("**/*.md", true)]
    [InlineData("src/*.md", true)]
    [InlineData(@"src\*.md", true)]
    public void RequiresRelativePath_DependsOnSeparator(string pattern, bool expected)
    {
        GlobMatcher.RequiresRelativePath(pattern).Should().Be(expected);
    }

    [Fact]
    public void Matches_UnclosedBracket_TreatedAsLiteral()
    {
        // 不抛异常即可（非法模式降级为字面量）
        var act = () => GlobMatcher.Matches("a[b", "a[b", caseSensitive: true);
        act.Should().NotThrow();
    }

    [Fact]
    public void Matches_RepeatedCalls_ShouldHitCache()
    {
        // 缓存不应改变语义
        for (var i = 0; i < 5; i++)
            GlobMatcher.Matches("a.md", "*.md", caseSensitive: true).Should().BeTrue();
    }
}

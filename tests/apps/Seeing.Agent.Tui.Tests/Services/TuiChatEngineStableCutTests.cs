using FluentAssertions;
using Seeing.Agent.Tui.Services;

namespace Seeing.Agent.Tui.Tests.Services;

/// <summary>
/// 「安全提交边界」纯函数测试：切点只在行边界、不处于未闭合围栏内。
/// </summary>
public sealed class TuiChatEngineStableCutTests
{
    public static IEnumerable<object[]> StableCutCases()
    {
        // 无任何安全边界：返回 fromOffset。
        yield return new object[] { "no boundary here", 0, 0 };

        // 段落空行（\n\n）之后。
        yield return new object[] { "para1\n\npara2", 0, 7 };

        // 未闭合围栏不切：返回围栏之前的安全边界。
        yield return new object[] { "para1\n\n```\ncode\n", 0, 7 };

        // 已闭合围栏：切在闭合围栏行末尾。
        yield return new object[] { "para1\n\n```\ncode\n```\nmore", 0, 20 };

        // fromOffset 之后无候选：返回 fromOffset，不得回退到更早边界。
        yield return new object[] { "a\n\nb\n\nc", 6, 6 };

        // 达 24 行上限：切在第 24 个换行之后（此处每行 2 字符，4? 见断言）。
        yield return new object[] { string.Concat(Enumerable.Repeat("x\n", 25)), 0, 48 };
    }

    [Theory]
    [MemberData(nameof(StableCutCases))]
    public void FindStableCut_ShouldReturnExpectedOffset(string text, int fromOffset, int expected)
    {
        TuiChatEngine.FindStableCut(text, fromOffset).Should().Be(expected);
    }

    [Fact]
    public void FindStableCut_UnclosedFenceTail_ShouldNotCutInsideFence()
    {
        var text = "para\n\n```\nunclosed code line\n";

        var cut = TuiChatEngine.FindStableCut(text, 0);

        cut.Should().Be(6);
        text[..cut].Should().NotContain("```");
    }
}

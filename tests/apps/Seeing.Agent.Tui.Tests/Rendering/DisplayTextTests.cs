using FluentAssertions;
using Seeing.Agent.Tui.Rendering;

namespace Seeing.Agent.Tui.Tests.Rendering;

/// <summary>
/// 显示宽度与截断辅助测试：状态栏必须恒为单行，宽度算错就会折行、挤掉输入行。
/// </summary>
public sealed class DisplayTextTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("abc", 3)]
    [InlineData("build", 5)]
    [InlineData("中文", 4)]
    [InlineData("空闲", 4)]
    [InlineData("C:\\ws\\proj", 10)]
    [InlineData("查询城市 · 要查哪个城市的天气？", 32)]
    public void Width_ShouldCountCjkAsTwoCells(string? value, int expected)
        => DisplayText.Width(value).Should().Be(expected);

    [Fact]
    public void TruncateMiddle_WhenFits_ShouldReturnUnchanged()
        => DisplayText.TruncateMiddle("C:\\ws\\proj", 40).Should().Be("C:\\ws\\proj");

    [Fact]
    public void TruncateMiddle_WhenTooLong_ShouldKeepHeadAndLeaf()
    {
        var value = "C:\\Users\\Administrator\\projects\\MyProjects\\Seeing.Agent";

        var truncated = DisplayText.TruncateMiddle(value, 30);

        DisplayText.Width(truncated).Should().BeLessThanOrEqualTo(30);
        truncated.Should().StartWith("C:\\");
        truncated.Should().Contain("…");
        truncated.Should().EndWith("Seeing.Agent");
    }

    [Fact]
    public void TruncateMiddle_WhenExtremelyNarrow_ShouldStillRespectWidth()
    {
        var value = "C:\\Users\\Administrator\\projects\\Seeing.Agent";

        var truncated = DisplayText.TruncateMiddle(value, 6);

        DisplayText.Width(truncated).Should().BeLessThanOrEqualTo(6);
    }

    [Fact]
    public void TruncateMiddle_WhenZeroWidth_ShouldReturnEmpty()
        => DisplayText.TruncateMiddle("C:\\ws", 0).Should().BeEmpty();

    [Fact]
    public void AbbreviateHome_ShouldReplaceHomePrefixWithTilde()
    {
        var home = Path.Combine("C:\\", "Users", "Administrator");
        var value = Path.Combine(home, "projects", "Seeing.Agent");

        DisplayText.AbbreviateHome(value, home).Should().Be("~\\projects\\Seeing.Agent");
        DisplayText.AbbreviateHome(home, home).Should().Be("~");
    }

    [Fact]
    public void AbbreviateHome_WhenOutsideHomeOrWithoutHome_ShouldReturnUnchanged()
    {
        var home = Path.Combine("C:\\", "Users", "Administrator");

        DisplayText.AbbreviateHome("D:\\other\\proj", home).Should().Be("D:\\other\\proj");
        DisplayText.AbbreviateHome("C:\\Users\\Administratorly\\proj", home).Should().Be("C:\\Users\\Administratorly\\proj");
        DisplayText.AbbreviateHome("D:\\other\\proj", null).Should().Be("D:\\other\\proj");
    }
}

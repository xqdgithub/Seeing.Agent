using FluentAssertions;
using Seeing.Agent.Tools.BuiltIn;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

public class OutputTruncatorTests
{
    [Fact]
    public void Truncate_WithinLimit_ShouldReturnUnchanged()
    {
        var input = "hello world";
        var result = OutputTruncator.Truncate(input, maxLines: 10, maxBytes: 1024, maxLineLength: 2000);

        result.Content.Should().Be(input);
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Truncate_ExceedsMaxLines_ShouldTruncate()
    {
        var input = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line {i}"));
        var result = OutputTruncator.Truncate(input, maxLines: 5, maxBytes: 102400, maxLineLength: 2000);

        result.Truncated.Should().BeTrue();
        result.KeptLines.Should().Be(5);
        result.TotalLines.Should().Be(100);
        result.TruncationMessage.Should().Contain("显示前 5 行");
    }

    [Fact]
    public void Truncate_ExceedsMaxBytes_ShouldTruncate()
    {
        var input = new string('x', 10000);
        var result = OutputTruncator.Truncate(input, maxLines: 100, maxBytes: 100, maxLineLength: 2000);

        result.Truncated.Should().BeTrue();
        result.TruncationMessage.Should().Contain("KB 限制");
    }

    [Fact]
    public void Truncate_ExceedsMaxLineLength_ShouldTruncateLine()
    {
        var input = new string('a', 500);
        var result = OutputTruncator.Truncate(input, maxLines: 10, maxBytes: 102400, maxLineLength: 50);

        result.Content.Should().Contain("行截断至 50 字符");
        result.Content.Length.Should().BeLessThan(200);
    }

    [Fact]
    public void Truncate_EmptyString_ShouldReturnEmpty()
    {
        var result = OutputTruncator.Truncate(string.Empty);

        result.Content.Should().Be(string.Empty);
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public void FormatWithLineNumbers_Basic_ShouldPrefixLines()
    {
        var input = "a\nb\nc";
        var result = OutputTruncator.FormatWithLineNumbers(input, startLine: 1);

        result.Should().Be("1: a\n2: b\n3: c");
    }
}

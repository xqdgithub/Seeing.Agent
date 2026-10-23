using FluentAssertions;
using Xunit;

namespace Seeing.Gateway.QQ.Tests;

public class QQTextSanitizerTests
{
    [Fact]
    public void Sanitize_ShouldStripUrls()
    {
        var (text, had) = QQTextSanitizer.Sanitize("打开 https://example.com/path 查看");
        had.Should().BeTrue();
        text.Should().Contain("[链接已省略]");
        text.Should().NotContain("https://");
    }

    [Fact]
    public void AggressiveSanitize_ShouldStripBareDomains()
    {
        var (text, had) = QQTextSanitizer.AggressiveSanitize("访问 example.com 即可");
        had.Should().BeTrue();
        text.Should().Contain("[链接已省略]");
    }

    [Fact]
    public void SplitChunks_ShouldRespectMaxLen()
    {
        var longText = new string('a', 4000);
        var chunks = QQTextSanitizer.SplitChunks(longText, maxLen: 1800).ToList();
        chunks.Should().HaveCountGreaterThan(1);
        chunks.Should().OnlyContain(c => c.Length <= 1800);
    }
}

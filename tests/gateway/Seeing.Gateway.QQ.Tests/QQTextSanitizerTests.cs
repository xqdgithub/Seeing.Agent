using FluentAssertions;
using Seeing.Gateway.QQ;
using Xunit;

namespace Seeing.Gateway.QQ.Tests;

public class QQTextSanitizerTests
{
    [Fact]
    public void ExtractImages_ShouldSupportImageTagAndMarkdown()
    {
        var text = "见下图 [Image: https://cdn.example.com/a.png] 以及 ![x](https://cdn.example.com/b.jpg) 结束";
        var (clean, urls) = QQTextSanitizer.ExtractImages(text);

        urls.Should().BeEquivalentTo(
            ["https://cdn.example.com/a.png", "https://cdn.example.com/b.jpg"]);
        clean.Should().Contain("见下图");
        clean.Should().Contain("结束");
        clean.Should().NotContain("https://");
    }

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

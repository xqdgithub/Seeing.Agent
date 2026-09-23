using FluentAssertions;
using Seeing.Agent.Tui.Services;

namespace Seeing.Agent.Tui.Tests.Services;

public sealed class TuiAttachmentResolverTests : IDisposable
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02];

    private readonly TuiAttachmentResolver _resolver = new();
    private readonly List<string> _tempFiles = [];

    public static IEnumerable<object[]> InlineParseCases()
    {
        yield return ["hello @a.png", false, "hello @a.png", Array.Empty<string>()];
        yield return ["plain text", false, "plain text", Array.Empty<string>()];
        yield return ["@", false, "@", Array.Empty<string>()];
        yield return ["@a.png summarize this", true, "summarize this", new[] { "a.png" }];
        yield return ["@a.png @b.txt rest", true, "rest", new[] { "a.png", "b.txt" }];
        yield return ["@a.png @b.txt", true, "", new[] { "a.png", "b.txt" }];
        yield return ["  @a.png rest", true, "rest", new[] { "a.png" }];
        yield return [@"@""C:\My Files\a.png"" describe", true, "describe", new[] { @"C:\My Files\a.png" }];
        yield return [@"@""a b"" @c", true, "", new[] { "a b", "c" }];
        yield return ["@a.png please look", true, "please look", new[] { "a.png" }];
        yield return [@"@C:\My Files\a.png rest", true, @"Files\a.png rest", new[] { @"C:\My" }];
        yield return ["look at @a.png", false, "look at @a.png", Array.Empty<string>()];
        yield return [@"@"""" x", false, @"@"""" x", Array.Empty<string>()];
    }

    [Theory]
    [MemberData(nameof(InlineParseCases))]
    public void TryParseInline_ShouldParseLeadingAttachments(
        string input, bool expectedResult, string expectedRemaining, string[] expectedPaths)
    {
        var result = _resolver.TryParseInline(input, out var remaining, out var paths);

        result.Should().Be(expectedResult);
        remaining.Should().Be(expectedRemaining);
        paths.Should().Equal(expectedPaths);
    }

    [Fact]
    public void TryParseInline_NullInput_ShouldReturnFalse()
    {
        var result = _resolver.TryParseInline(null!, out var remaining, out var paths);

        result.Should().BeFalse();
        remaining.Should().BeEmpty();
        paths.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_KnownImageExtension_ShouldReturnBase64AndMime()
    {
        var path = CreateTempFile(".png", PngBytes);

        var attachment = await _resolver.LoadAsync(path, TestContext.Current.CancellationToken);

        attachment.FileName.Should().Be(Path.GetFileName(path));
        attachment.MimeType.Should().Be("image/png");
        attachment.Size.Should().Be(PngBytes.Length);
        attachment.Base64Data.Should().Be(Convert.ToBase64String(PngBytes));
    }

    [Theory]
    [InlineData(".txt", "hello world", "text/plain")]
    [InlineData(".md", "# title", "text/markdown")]
    [InlineData(".json", "{}", "application/json")]
    [InlineData(".csv", "a,b", "text/csv")]
    [InlineData(".pdf", "%PDF-1.4", "application/pdf")]
    [InlineData(".webp", "RIFF____WEBP", "image/webp")]
    public async Task LoadAsync_MapsExtensionToMime(string extension, string content, string expectedMime)
    {
        var path = CreateTempFile(extension, System.Text.Encoding.UTF8.GetBytes(content));

        var attachment = await _resolver.LoadAsync(path, TestContext.Current.CancellationToken);

        attachment.MimeType.Should().Be(expectedMime);
    }

    [Fact]
    public async Task LoadAsync_UnknownExtensionBinaryContent_ShouldFallbackToOctetStream()
    {
        var path = CreateTempFile(".weird", [0x00, 0x01, 0x02, 0x03, 0xFF]);

        var attachment = await _resolver.LoadAsync(path, TestContext.Current.CancellationToken);

        attachment.MimeType.Should().Be("application/octet-stream");
    }

    [Fact]
    public async Task LoadAsync_NoExtensionWithPngMagic_ShouldSniffImagePng()
    {
        var path = CreateTempFile("", PngBytes);

        var attachment = await _resolver.LoadAsync(path, TestContext.Current.CancellationToken);

        attachment.MimeType.Should().Be("image/png");
    }

    [Fact]
    public async Task LoadAsync_EmptyFile_ShouldReturnEmptyBase64AndZeroSize()
    {
        var path = CreateTempFile(".bin", []);

        var attachment = await _resolver.LoadAsync(path, TestContext.Current.CancellationToken);

        attachment.Base64Data.Should().BeEmpty();
        attachment.Size.Should().Be(0);
    }

    [Fact]
    public async Task LoadAsync_MissingFile_ShouldThrowFileNotFound()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.png");

        var act = async () => await _resolver.LoadAsync(missing);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("IMAGE/JPEG", true)]
    [InlineData("image/svg+xml", true)]
    [InlineData("text/plain", false)]
    [InlineData("application/octet-stream", false)]
    [InlineData("", false)]
    public void IsImage_ShouldClassifyByMimePrefix(string mime, bool expected)
        => TuiAttachmentResolver.IsImage(mime).Should().Be(expected);

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1572864, "1.5 MB")]
    [InlineData(1073741824, "1 GB")]
    public void FormatSize_ShouldRenderHumanReadable(long bytes, string expected)
        => TuiAttachmentResolver.FormatSize(bytes).Should().Be(expected);

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch (IOException)
            {
                // 临时文件清理失败不阻断测试
            }
        }
    }

    private string CreateTempFile(string extension, byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"seeing-tui-attach-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, content);
        _tempFiles.Add(path);
        return path;
    }
}

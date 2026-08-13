using FluentAssertions;
using Seeing.Agent.Tools.BuiltIn.FileSystem;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

public class FileSystemHelperTests
{
    [Fact]
    public void IsPathWithinDirectory_SubPath_ShouldReturnTrue()
    {
        var result = FileSystemHelper.IsPathWithinDirectory(
            @"C:\workspace\sub\file.txt",
            @"C:\workspace");

        result.Should().BeTrue();
    }

    [Fact]
    public void IsPathWithinDirectory_OutsidePath_ShouldReturnFalse()
    {
        var result = FileSystemHelper.IsPathWithinDirectory(
            @"C:\other\file.txt",
            @"C:\workspace");

        result.Should().BeFalse();
    }

    [Fact]
    public void IsPathWithinDirectory_SameDirectory_ShouldReturnTrue()
    {
        var result = FileSystemHelper.IsPathWithinDirectory(
            @"C:\workspace\file.txt",
            @"C:\workspace");

        result.Should().BeTrue();
    }

    [Fact]
    public void IsPathWithinDirectory_CaseInsensitive_ShouldReturnTrue()
    {
        var result = FileSystemHelper.IsPathWithinDirectory(
            @"C:\WORKSPACE\file.txt",
            @"c:\workspace");

        result.Should().BeTrue();
    }

    [Fact]
    public void IsPathWithinDirectory_InvalidPath_ShouldReturnFalse()
    {
        var result = FileSystemHelper.IsPathWithinDirectory(
            string.Empty,
            @"C:\workspace");

        result.Should().BeFalse();
    }

    [Fact]
    public void IsPathWithinDirectory_SiblingPrefix_ShouldReturnFalse()
    {
        // C:\foo 不是 C:\foobar 的子路径（前缀误判回归）
        FileSystemHelper.IsPathWithinDirectory(@"C:\foo", @"C:\foobar").Should().BeFalse();
    }

    [Fact]
    public void IsPathWithinDirectory_SiblingPrefixReversed_ShouldReturnFalse()
    {
        FileSystemHelper.IsPathWithinDirectory(@"C:\foobar", @"C:\foo").Should().BeFalse();
    }

    [Fact]
    public void IsPathWithinDirectory_ExactDirectory_ShouldReturnTrue()
    {
        // 目录本身也算"在目录内"
        FileSystemHelper.IsPathWithinDirectory(@"C:\workspace", @"C:\workspace").Should().BeTrue();
    }

    [Fact]
    public void IsPathWithinDirectory_NestedSubdir_ShouldReturnTrue()
    {
        FileSystemHelper.IsPathWithinDirectory(@"C:\workspace\a\b", @"C:\workspace").Should().BeTrue();
    }

    [Fact]
    public void GetMimeType_Png_ShouldReturnImagePng()
    {
        var result = FileSystemHelper.GetMimeType("logo.png");

        result.Should().Be("image/png");
    }

    [Fact]
    public void GetMimeType_Cs_ShouldReturnTextXCsharp()
    {
        var result = FileSystemHelper.GetMimeType("Program.cs");

        result.Should().Be("text/x-csharp");
    }

    [Fact]
    public void GetMimeType_Unknown_ShouldReturnApplicationOctetStream()
    {
        var result = FileSystemHelper.GetMimeType("file.unknown");

        result.Should().Be("application/octet-stream");
    }

    [Fact]
    public void IsBinaryByExtension_Png_ShouldReturnTrue()
    {
        var result = FileSystemHelper.IsBinaryByExtension("image.png");

        result.Should().BeTrue();
    }

    [Fact]
    public void IsBinaryByExtension_Txt_ShouldReturnFalse()
    {
        var result = FileSystemHelper.IsBinaryByExtension("readme.txt");

        result.Should().BeFalse();
    }

    [Fact]
    public void IsImage_Png_ShouldReturnTrue()
    {
        var result = FileSystemHelper.IsImage("photo.png");

        result.Should().BeTrue();
    }

    [Fact]
    public void IsImage_Svg_ShouldReturnFalse()
    {
        var result = FileSystemHelper.IsImage("icon.svg");

        result.Should().BeFalse();
    }

    [Fact]
    public void IsPdf_Pdf_ShouldReturnTrue()
    {
        var result = FileSystemHelper.IsPdf("doc.pdf");

        result.Should().BeTrue();
    }

    [Fact]
    public void TruncateLine_ShortLine_ShouldReturnUnchanged()
    {
        var result = FileSystemHelper.TruncateLine("short");

        result.Should().Be("short");
    }

    [Fact]
    public void TruncateLine_LongLine_ShouldTruncate()
    {
        var longLine = new string('x', FileSystemHelper.MaxLineLength + 10);
        var result = FileSystemHelper.TruncateLine(longLine);

        result.Length.Should().Be(FileSystemHelper.MaxLineLength + FileSystemHelper.MaxLineSuffix.Length);
        result.Should().Contain(FileSystemHelper.MaxLineSuffix);
    }

    [Fact]
    public void EscapeXml_SpecialChars_ShouldEscape()
    {
        var result = FileSystemHelper.EscapeXml("<tag attr=\"value\">&amp;</tag>");

        result.Should().Be("&lt;tag attr=&quot;value&quot;&gt;&amp;amp;&lt;/tag&gt;");
    }

    [Fact]
    public void NormalizeLineEndings_Crlf_ShouldConvertToLf()
    {
        var result = FileSystemHelper.NormalizeLineEndings("a\r\nb\r\nc");

        result.Should().Be("a\nb\nc");
    }

    [Fact]
    public void NormalizeLineEndings_Mixed_ShouldConvertToLf()
    {
        var result = FileSystemHelper.NormalizeLineEndings("a\r\nb\rc");

        result.Should().Be("a\nb\nc");
    }

    [Fact]
    public void LevenshteinDistance_SameStrings_ShouldReturnZero()
    {
        var result = FileSystemHelper.LevenshteinDistance("hello", "hello");

        result.Should().Be(0);
    }

    [Fact]
    public void LevenshteinDistance_DifferentStrings_ShouldReturnDistance()
    {
        var result = FileSystemHelper.LevenshteinDistance("kitten", "sitting");

        result.Should().Be(3);
    }

    [Fact]
    public void LevenshteinDistance_EmptyString_ShouldReturnLength()
    {
        var result = FileSystemHelper.LevenshteinDistance("", "hello");

        result.Should().Be(5);
    }

    [Fact]
    public void MatchesGlobPattern_ExactMatch_ShouldReturnTrue()
    {
        var result = FileSystemHelper.MatchesGlobPattern("file.cs", "file.cs");

        result.Should().BeTrue();
    }

    [Fact]
    public void MatchesGlobPattern_Wildcard_ShouldMatch()
    {
        var result = FileSystemHelper.MatchesGlobPattern("file.cs", "*.cs");

        result.Should().BeTrue();
    }

    [Fact]
    public void MatchesGlobPattern_NoMatch_ShouldReturnFalse()
    {
        var result = FileSystemHelper.MatchesGlobPattern("file.cs", "*.txt");

        result.Should().BeFalse();
    }
}

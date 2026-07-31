using FluentAssertions;
using Seeing.Agent.Tools.BuiltIn;
using Xunit;

namespace Seeing.Agent.Tests.Detection;

public class BinaryFileDetectorTests
{
    [Fact]
    public void IsBinaryFile_PngExtension_ShouldReturnTrue()
    {
        BinaryFileDetector.IsBinaryFile("/path/to/image.png").Should().BeTrue();
    }

    [Fact]
    public void IsBinaryFile_JpgExtension_ShouldReturnTrue()
    {
        BinaryFileDetector.IsBinaryFile("photo.jpg").Should().BeTrue();
    }

    [Fact]
    public void IsBinaryFile_PdfExtension_ShouldReturnTrue()
    {
        BinaryFileDetector.IsBinaryFile("doc.pdf").Should().BeTrue();
    }

    [Fact]
    public void IsBinaryFile_DllExtension_ShouldReturnTrue()
    {
        BinaryFileDetector.IsBinaryFile("lib.dll").Should().BeTrue();
    }

    [Fact]
    public void IsBinaryFile_ExeExtension_ShouldReturnTrue()
    {
        BinaryFileDetector.IsBinaryFile("app.exe").Should().BeTrue();
    }

    [Fact]
    public void IsBinaryFile_TxtExtension_WithoutExistingFile_ShouldFallbackToBinary()
    {
        // 扩展名不在二进制列表中时，回退到内容检测；不存在的文件异常后默认按二进制处理
        BinaryFileDetector.IsBinaryFile("readme.txt").Should().BeTrue();
    }

    [Fact]
    public void IsBinaryFile_CsExtension_WithoutExistingFile_ShouldFallbackToBinary()
    {
        BinaryFileDetector.IsBinaryFile("Program.cs").Should().BeTrue();
    }

    [Fact]
    public void IsImageFile_TxtExtension_ShouldReturnFalse()
    {
        BinaryFileDetector.IsImageFile("readme.txt").Should().BeFalse();
    }

    [Fact]
    public void IsImageFile_CsExtension_ShouldReturnFalse()
    {
        BinaryFileDetector.IsImageFile("Program.cs").Should().BeFalse();
    }

    [Fact]
    public void IsImageFile_Png_ShouldReturnTrue()
    {
        BinaryFileDetector.IsImageFile("logo.png").Should().BeTrue();
    }

    [Fact]
    public void IsImageFile_Pdf_ShouldReturnFalse()
    {
        BinaryFileDetector.IsImageFile("doc.pdf").Should().BeFalse();
    }

    [Fact]
    public void GetMimeType_Png_ShouldReturnImagePng()
    {
        BinaryFileDetector.GetMimeType("logo.png").Should().Be("image/png");
    }

    [Fact]
    public void GetMimeType_Cs_ShouldReturnApplicationOctetStream()
    {
        BinaryFileDetector.GetMimeType("Program.cs").Should().Be("application/octet-stream");
    }
}

using FluentAssertions;
using Seeing.Agent.Tools.Support;
using Xunit;

namespace Seeing.Agent.Tools.Support.Tests;

public class FileContentSupportTests
{
    [Theory]
    [InlineData("a.png", "image/png")]
    [InlineData("a.md", "text/markdown")]
    [InlineData("a.unknownext", "application/octet-stream")]
    public void GetMimeType_ShouldMap(string name, string expected)
        => FileContentSupport.GetMimeType(name).Should().Be(expected);

    [Theory]
    [InlineData("a.exe", true)]
    [InlineData("a.cs", false)]
    public void IsBinaryByExtension_ShouldDetect(string name, bool expected)
        => FileContentSupport.IsBinaryByExtension(name).Should().Be(expected);
}

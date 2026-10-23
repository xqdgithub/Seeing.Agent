using FluentAssertions;
using Seeing.Agent.WebUI.Models;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Models;

public class AttachmentClassificationTests
{
    [Theory]
    [InlineData("image/png", "a.bin", true)]
    [InlineData("", "a.PNG", true)]
    [InlineData("application/pdf", "a.pdf", false)]
    public void IsImage_ShouldUseContentTypeThenExtension(string type, string name, bool expected)
        => AttachmentClassifier.IsImage(type, name).Should().Be(expected);
}

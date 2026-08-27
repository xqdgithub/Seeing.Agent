using FluentAssertions;
using Seeing.Agent.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class ToolOutputOptionsTests
{
    [Fact]
    public void Defaults_ShouldBeSane()
    {
        var options = new SeeingAgentOptions().ToolOutput;

        options.Enabled.Should().BeTrue();
        options.MaxInlineBytes.Should().Be(50 * 1024);
        options.PreviewHeadChars.Should().Be(1024);
        options.PreviewTailChars.Should().Be(1024);
    }
}

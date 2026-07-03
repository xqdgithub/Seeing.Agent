using FluentAssertions;
using Seeing.Agent.Acp.Backends;
using Xunit;

namespace Seeing.Agent.Acp.Tests;

public class AcpExecutableResolverTests
{
    [Fact]
    public void Resolve_AbsolutePath_ShouldReturnExistingFile()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"acp-test-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(tempFile, "@echo off\r\n");

        try
        {
            var resolved = AcpExecutableResolver.Resolve(tempFile);
            resolved.Should().Be(Path.GetFullPath(tempFile));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Resolve_BareCommandName_ShouldReturnUnchanged()
    {
        AcpExecutableResolver.Resolve("opencode").Should().Be("opencode");
    }
}

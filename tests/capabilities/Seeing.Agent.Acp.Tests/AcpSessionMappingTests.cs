using FluentAssertions;
using Seeing.Agent.Acp.Session;
using Xunit;

namespace Seeing.Agent.Acp.Tests;

public class AcpSessionMappingTests
{
    [Fact]
    public void TryParse_ValidValue_ShouldReturnMapping()
    {
        var mapping = AcpSessionMapping.TryParse("opencode|sess_abc123");

        mapping.Should().NotBeNull();
        mapping!.BackendId.Should().Be("opencode");
        mapping.AcpSessionId.Should().Be("sess_abc123");
    }

    [Fact]
    public void TryParse_EmptySessionId_ShouldReturnNull()
    {
        AcpSessionMapping.TryParse("opencode|").Should().BeNull();
    }

    [Fact]
    public void Serialize_ShouldRoundTrip()
    {
        var original = new AcpSessionMapping
        {
            BackendId = "opencode",
            AcpSessionId = "sess_abc123"
        };

        AcpSessionMapping.TryParse(original.Serialize())
            .Should()
            .BeEquivalentTo(original);
    }
}

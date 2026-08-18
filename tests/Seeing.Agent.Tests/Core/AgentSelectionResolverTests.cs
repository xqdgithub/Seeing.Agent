using FluentAssertions;
using Moq;
using Seeing.Agent.Core;
using Xunit;

using Seeing.Agent.Abstractions.Agents;
namespace Seeing.Agent.Tests.Core;

public class AgentSelectionResolverTests
{
    [Fact]
    public void ResolveAcpModeId_ShouldPreferRequestOverSession()
    {
        var resolver = CreateResolver();

        var result = resolver.ResolveAcpModeId(" build ", "ask");

        result.Should().Be("build");
    }

    [Fact]
    public void ResolveAcpModeId_WhenRequestMissing_ShouldUseSession()
    {
        var resolver = CreateResolver();

        var result = resolver.ResolveAcpModeId(null, " plan ");

        result.Should().Be("plan");
    }

    [Fact]
    public async Task ResolveAgentIdAsync_ShouldPreferRequestOverSessionAndDefault()
    {
        var runtime = new Mock<IAgentRuntimeManager>();
        runtime.Setup(r => r.GetDefaultAgentNameAsync()).ReturnsAsync("default-agent");
        var resolver = new AgentSelectionResolver(runtime.Object);

        var result = await resolver.ResolveAgentIdAsync("request-agent", "session-agent");

        result.Should().Be("request-agent");
    }

    private static AgentSelectionResolver CreateResolver()
    {
        var runtime = new Mock<IAgentRuntimeManager>();
        runtime.Setup(r => r.GetDefaultAgentNameAsync()).ReturnsAsync("build");
        return new AgentSelectionResolver(runtime.Object);
    }
}

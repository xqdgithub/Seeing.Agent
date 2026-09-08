using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Acp.Backends;
using Seeing.Agent.Acp.Configuration;
using Xunit;

namespace Seeing.Agent.Acp.Tests;

public class AcpBackendRegistryTests
{
    [Fact]
    public void GetBackend_WithValidConfig_ShouldReturnDescriptor()
    {
        var registry = CreateRegistry(new AcpOptions
        {
            Enabled = true,
            DefaultBackend = "opencode",
            Backends = new Dictionary<string, AcpBackendConfig>
            {
                ["opencode"] = new() { Command = "opencode", Args = new List<string> { "acp" } }
            }
        });

        var backend = registry.GetBackend("opencode");

        backend.Command.Should().NotBeNullOrWhiteSpace();
        Path.GetFileName(backend.Command).Should().StartWith("opencode");
        backend.Args.Should().Contain("acp");
    }

    [Fact]
    public void ResolveDefault_ShouldUseConfiguredDefault()
    {
        var registry = CreateRegistry(new AcpOptions
        {
            Enabled = true,
            DefaultBackend = "codex",
            Backends = new Dictionary<string, AcpBackendConfig>
            {
                ["codex"] = new() { Command = "codex" },
                ["opencode"] = new() { Command = "opencode" }
            }
        });

        registry.ResolveDefault().Should().Be("codex");
    }

    [Fact]
    public void GetBackend_WhenDisabled_ShouldThrow()
    {
        var registry = CreateRegistry(new AcpOptions { Enabled = false });

        var act = () => registry.GetBackend("any");
        act.Should().Throw<InvalidOperationException>();
    }

    private static AcpBackendRegistry CreateRegistry(AcpOptions acp)
    {
        var options = Mock.Of<IOptionsMonitor<AcpOptions>>(m => m.CurrentValue == acp);
        return new AcpBackendRegistry(options, NullLogger<AcpBackendRegistry>.Instance);
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Acp.Backends;
using Seeing.Agent.Configuration;
using Xunit;

namespace Seeing.Agent.Acp.Tests;

public class AcpBackendRegistryTests
{
    [Fact]
    public void GetBackend_WithValidConfig_ShouldReturnDescriptor()
    {
        var registry = CreateRegistry(new SeeingAgentOptions
        {
            Acp = new AcpOptions
            {
                Enabled = true,
                DefaultBackend = "opencode",
                Backends = new Dictionary<string, AcpBackendConfig>
                {
                    ["opencode"] = new() { Command = "opencode", Args = new List<string> { "acp" } }
                }
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
        var registry = CreateRegistry(new SeeingAgentOptions
        {
            Acp = new AcpOptions
            {
                Enabled = true,
                DefaultBackend = "codex",
                Backends = new Dictionary<string, AcpBackendConfig>
                {
                    ["codex"] = new() { Command = "codex" },
                    ["opencode"] = new() { Command = "opencode" }
                }
            }
        });

        registry.ResolveDefault().Should().Be("codex");
    }

    [Fact]
    public void GetBackend_WhenDisabled_ShouldThrow()
    {
        var registry = CreateRegistry(new SeeingAgentOptions
        {
            Acp = new AcpOptions { Enabled = false }
        });

        var act = () => registry.GetBackend("any");
        act.Should().Throw<InvalidOperationException>();
    }

    private static AcpBackendRegistry CreateRegistry(SeeingAgentOptions options)
    {
        var workspaceMock = new Mock<IWorkspaceProvider>();
        workspaceMock.Setup(w => w.WorkspaceRoot).Returns(".");
        workspaceMock.Setup(w => w.UserSeeingDirectory).Returns(Path.GetTempPath());
        workspaceMock.Setup(w => w.ProjectSeeingDirectory).Returns(Path.GetTempPath());

        var configManager = new UnifiedConfigManager(
            workspaceMock.Object,
            NullLogger<UnifiedConfigManager>.Instance);

        // Set the Acp options
        configManager.GetSeeingAgentOptions().Acp = options.Acp;

        return new AcpBackendRegistry(configManager, NullLogger<AcpBackendRegistry>.Instance);
    }
}

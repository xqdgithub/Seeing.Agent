using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Acp.Backends;
using Seeing.Agent.Acp.Hosting;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Xunit;

namespace Seeing.Agent.Acp.Tests;

public class AcpDynamicAgentRegistrarTests
{
    [Fact]
    public async Task RegisterAsync_ShouldCreateAgentPerBackend()
    {
        var registry = new Mock<IAgentRegistry>();
        var registered = new List<AgentDefinition>();
        registry.Setup(r => r.HasAgent(It.IsAny<string>())).Returns(false);
        registry.Setup(r => r.GetAgentsAsync()).ReturnsAsync(Array.Empty<AgentDefinition>());
        registry.Setup(r => r.RegisterAgentAsync(It.IsAny<AgentDefinition>()))
            .Callback<AgentDefinition>(registered.Add)
            .Returns(Task.CompletedTask);

        var backendRegistry = CreateBackendRegistry(new SeeingAgentOptions
        {
            Acp = new AcpOptions
            {
                Enabled = true,
                Backends = new Dictionary<string, AcpBackendConfig>
                {
                    ["opencode"] = new() { Command = "opencode", Args = new List<string> { "acp" } },
                    ["codex"] = new() { Command = "codex", Args = new List<string> { "acp" } }
                }
            }
        });

        await AcpDynamicAgentRegistrar.RegisterAsync(
            registry.Object,
            backendRegistry,
            Options.Create(new SeeingAgentOptions { Acp = new AcpOptions { Enabled = true } }),
            NullLogger.Instance);

        registered.Should().HaveCount(2);
        registered.Select(a => a.Name).Should().BeEquivalentTo(["acp-opencode", "acp-codex"]);
        registered.Should().OnlyContain(a =>
            a.Runtime == AgentRuntime.AcpPassthrough &&
            a.AcpBackend != null &&
            a.Tags.Contains(AcpDynamicAgentRegistrar.AutoTag));
    }

    [Fact]
    public void GetAgentName_ShouldUseBackendId()
    {
        AcpDynamicAgentRegistrar.GetAgentName("opencode").Should().Be("acp-opencode");
    }

    private static AcpBackendRegistry CreateBackendRegistry(SeeingAgentOptions options)
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

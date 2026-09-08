using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Acp.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class ConfigSectionRegistryConsumptionTests
{
    [Fact]
    public void CreateWithSpine_Should_Only_Contain_Spine_Sections()
    {
        var registry = ConfigSectionRegistry.CreateWithSpine();
        var keys = registry.Sections.Select(s => s.Key).OrderBy(k => k).ToList();

        keys.Should().Equal(
            "AgentModels",
            "DefaultAgent",
            "DefaultModel",
            "GlobalWorkspaceRoot",
            "Modules",
            "Permission",
            "PluginEnabled",
            "Plugins",
            "Providers",
            "Scenario",
            "Scenarios",
            "Seams",
            "TitleGeneration",
            "ToolOutput",
            "Workspace");

        keys.Should().NotContain("Acp");
        keys.Should().NotContain("Gateway");
        keys.Should().NotContain("Scheduler");
        keys.Should().NotContain("Memory");
    }

    [Fact]
    public void Module_Can_Register_Section_Onto_Shared_Registry()
    {
        var services = new ServiceCollection();
        var registry = services.GetOrCreateConfigSectionRegistry();

        registry.Register(new ConfigSectionMeta(
            AcpOptions.SectionName, "seeing.json", ConfigScope.UserOnly, typeof(AcpOptions)));

        registry.TryGet("Acp", out var meta).Should().BeTrue();
        meta.SectionType.Should().Be(typeof(AcpOptions));
        meta.Scope.Should().Be(ConfigScope.UserOnly);

        services.GetOrCreateConfigSectionRegistry().Should().BeSameAs(registry);
    }

    [Fact]
    public async Task UnifiedConfigManager_GetSection_Should_Use_Registry_Cache_Not_SeeingAgent_Switch()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ucm-registry-" + Guid.NewGuid().ToString("N"));
        var userSeeing = Path.Combine(tempDir, "user", ".seeing");
        var projectSeeing = Path.Combine(tempDir, "project", ".seeing");
        Directory.CreateDirectory(userSeeing);
        Directory.CreateDirectory(projectSeeing);
        await File.WriteAllTextAsync(Path.Combine(projectSeeing, "seeing.json"),
            """
            {
              "SeeingAgent": {
                "Permission": { "AutoApproveAll": true }
              }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(userSeeing, "seeing.json"),
            """
            {
              "SeeingAgent": {
                "Acp": { "Enabled": true, "DefaultBackend": "cursor" }
              }
            }
            """);

        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(w => w.UserSeeingDirectory).Returns(userSeeing);
        workspace.Setup(w => w.ProjectSeeingDirectory).Returns(projectSeeing);

        var registry = TestConfigSectionRegistry.WithAcp();
        var manager = new UnifiedConfigManager(workspace.Object, NullLogger<UnifiedConfigManager>.Instance, registry);
        await manager.LoadAsync();

        manager.GetSection<PermissionOptions>("Permission").AutoApproveAll.Should().BeTrue();
        manager.GetSection<AcpOptions>("Acp").DefaultBackend.Should().Be("cursor");

        var act = () => manager.SaveSectionAsync("NotRegistered", new PermissionOptions(), ConfigLevel.Project);
        await act.Should().ThrowAsync<ArgumentException>();

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public void AddSeeingCore_Factory_Should_Not_Sync_Load()
    {
        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(w => w.UserSeeingDirectory).Returns(Path.GetTempPath());
        workspace.Setup(w => w.ProjectSeeingDirectory).Returns(Path.GetTempPath());

        var manager = new UnifiedConfigManager(
            workspace.Object,
            NullLogger<UnifiedConfigManager>.Instance,
            ConfigSectionRegistry.CreateWithSpine());

        manager.SeeingAgent.DefaultAgent.Should().BeNull();
        manager.GetSectionMeta("Providers").Should().NotBeNull();
        manager.GetSectionMeta("Acp").Should().BeNull();
    }
}

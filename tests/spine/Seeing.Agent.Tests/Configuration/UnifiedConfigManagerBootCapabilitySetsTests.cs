using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Core.CapabilitySets;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

/// <summary>
/// Boot / CapabilitySets 配置节 save/load round-trip 与 null 删除键（OpenSpec 5.1–5.2）。
/// </summary>
public class UnifiedConfigManagerBootCapabilitySetsTests
{
    private static (UnifiedConfigManager Manager, string UserSeeing, string TempDir) CreateManager()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ucm-boot-caps-" + Guid.NewGuid().ToString("N"));
        var userSeeing = Path.Combine(tempDir, ".seeing");
        Directory.CreateDirectory(userSeeing);

        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(w => w.UserSeeingDirectory).Returns(userSeeing);
        workspace.Setup(w => w.ProjectSeeingDirectory).Returns(userSeeing);

        var manager = new UnifiedConfigManager(
            workspace.Object,
            NullLogger<UnifiedConfigManager>.Instance,
            ConfigSectionRegistry.CreateWithSpine());
        return (manager, userSeeing, tempDir);
    }

    [Fact]
    public async Task SaveSectionsAsync_BootAndCapabilitySets_ShouldRoundTrip_WithPascalCaseKeys()
    {
        var (manager, userSeeing, tempDir) = CreateManager();
        try
        {
            var capabilitySets = new Dictionary<string, CapabilitySetConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["secure"] = new CapabilitySetConfig
                {
                    Modules = ["*"],
                    Disabled = ["shell", "mcp"],
                },
            };

            await manager.SaveSectionsAsync(ConfigLevel.Project, new Dictionary<string, object>
            {
                ["Boot"] = "secure",
                ["CapabilitySets"] = capabilitySets,
            });

            var path = Path.Combine(userSeeing, "seeing.json");
            File.Exists(path).Should().BeTrue();
            var raw = await File.ReadAllTextAsync(path);
            raw.Should().Contain("\"Boot\"");
            raw.Should().Contain("\"CapabilitySets\"");
            raw.Should().Contain("\"Modules\"");
            raw.Should().Contain("\"Disabled\"");
            // 禁止 camelCase 键
            raw.Should().NotContain("\"boot\"");
            raw.Should().NotContain("\"capabilitySets\"");
            raw.Should().NotContain("\"modules\"");
            raw.Should().NotContain("\"disabled\"");

            var root = JsonNode.Parse(raw)!.AsObject();
            var seeing = root["SeeingAgent"]!.AsObject();
            seeing["Boot"]!.GetValue<string>().Should().Be("secure");
            seeing["CapabilitySets"]!["secure"]!["Modules"]![0]!.GetValue<string>().Should().Be("*");
            seeing["CapabilitySets"]!["secure"]!["Disabled"]!.AsArray()
                .Select(n => n!.GetValue<string>())
                .Should().BeEquivalentTo(["shell", "mcp"]);

            await manager.ReloadAsync();
            manager.SeeingAgent.Boot.Should().Be("secure");
            manager.SeeingAgent.CapabilitySets.Should().ContainKey("secure");
            manager.SeeingAgent.CapabilitySets["secure"].Modules.Should().Equal("*");
            manager.SeeingAgent.CapabilitySets["secure"].Disabled.Should().BeEquivalentTo(["shell", "mcp"]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RemoveSeeingAgentKeysAsync_BootAndCapabilitySets_ShouldDeleteKeysFromJson()
    {
        var (manager, userSeeing, tempDir) = CreateManager();
        try
        {
            await manager.SaveSectionsAsync(ConfigLevel.Project, new Dictionary<string, object>
            {
                ["Boot"] = "minimal",
                ["CapabilitySets"] = new Dictionary<string, CapabilitySetConfig>
                {
                    ["custom"] = new() { Modules = ["io.local", "basic"] },
                },
            });

            // SaveLevel 语义：null → RemoveSeeingAgentKeys
            await manager.RemoveSeeingAgentKeysAsync(ConfigLevel.Project, ["Boot", "CapabilitySets"]);

            var raw = await File.ReadAllTextAsync(Path.Combine(userSeeing, "seeing.json"));
            var seeing = JsonNode.Parse(raw)!["SeeingAgent"]!.AsObject();
            seeing.ContainsKey("Boot").Should().BeFalse();
            seeing.ContainsKey("CapabilitySets").Should().BeFalse();

            manager.SeeingAgent.Boot.Should().BeNull();
            manager.SeeingAgent.CapabilitySets.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ExistingPascalCaseBootJson_ShouldPopulateOptions()
    {
        var (manager, userSeeing, tempDir) = CreateManager();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(userSeeing, "seeing.json"),
                """
                {
                  "SeeingAgent": {
                    "Boot": "code",
                    "CapabilitySets": {
                      "code": {
                        "Modules": ["io.local", "filesystem"],
                        "Disabled": ["git"]
                      }
                    }
                  }
                }
                """);

            await manager.LoadAsync();

            manager.SeeingAgent.Boot.Should().Be("code");
            manager.SeeingAgent.CapabilitySets.Should().ContainKey("code");
            manager.SeeingAgent.CapabilitySets["code"].Modules.Should().Equal("io.local", "filesystem");
            manager.SeeingAgent.CapabilitySets["code"].Disabled.Should().Equal("git");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class AcpConfigPersistenceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task PatchProjectAcpSection_RoundTripsThroughUnifiedConfigManager()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "acp-persist-" + Guid.NewGuid().ToString("N"));
        var projectSeeing = Path.Combine(tempDir, ".seeing");
        Directory.CreateDirectory(projectSeeing);
        var projectPath = Path.Combine(projectSeeing, "seeing.json");

        File.WriteAllText(projectPath,
            """
            {
              "SeeingAgent": {
                "Gateway": { "Enabled": true, "Port": 8765 },
                "Acp": {
                  "Enabled": true,
                  "Backends": {
                    "cursor": { "Command": "C:/old.cmd", "Args": ["acp"] }
                  }
                }
              }
            }
            """);

        var payload = new AcpOptions
        {
            Enabled = true,
            DefaultBackend = "cursor",
            Backends = new Dictionary<string, AcpBackendConfig>
            {
                ["cursor"] = new() { Command = "C:/saved.cmd", Args = new List<string> { "acp" } }
            }
        };

        var root = JsonNode.Parse(File.ReadAllText(projectPath))!.AsObject();
        var seeingAgent = root["SeeingAgent"] as JsonObject ?? new JsonObject();
        seeingAgent["Acp"] = JsonSerializer.SerializeToNode(payload, JsonOptions);
        root["SeeingAgent"] = seeingAgent;
        File.WriteAllText(projectPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var saved = File.ReadAllText(projectPath);
        saved.Should().Contain("C:/saved.cmd");

        var workspaceMock = new Mock<IWorkspaceProvider>();
        workspaceMock.Setup(w => w.WorkspaceRoot).Returns(tempDir);
        workspaceMock.Setup(w => w.UserSeeingDirectory).Returns(Path.GetTempPath());
        workspaceMock.Setup(w => w.ProjectSeeingDirectory).Returns(projectSeeing);

        var configManager = new UnifiedConfigManager(
            workspaceMock.Object,
            NullLogger<UnifiedConfigManager>.Instance);

        await configManager.LoadAsync();

        configManager.GetSeeingAgentOptions().Acp.Backends["cursor"].Command.Should().Be("C:/saved.cmd");

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task Merge_UserAndProjectBackends_ProjectCommandOverridesUser()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "acp-merge-" + Guid.NewGuid().ToString("N"));
        var userSeeing = Path.Combine(tempDir, "user", ".seeing");
        var projectSeeing = Path.Combine(tempDir, "project", ".seeing");
        Directory.CreateDirectory(userSeeing);
        Directory.CreateDirectory(projectSeeing);

        File.WriteAllText(Path.Combine(userSeeing, "seeing.json"),
            """
            {
              "SeeingAgent": {
                "Acp": {
                  "Enabled": true,
                  "Backends": {
                    "cursor": { "Command": "C:/user.cmd", "Args": ["acp"] }
                  }
                }
              }
            }
            """);

        File.WriteAllText(Path.Combine(projectSeeing, "seeing.json"),
            """
            {
              "SeeingAgent": {
                "Acp": {
                  "Enabled": true,
                  "Backends": {
                    "cursor": { "Command": "C:/project.cmd", "Args": ["acp"] }
                  }
                }
              }
            }
            """);

        var workspaceMock = new Mock<IWorkspaceProvider>();
        workspaceMock.Setup(w => w.WorkspaceRoot).Returns(tempDir);
        workspaceMock.Setup(w => w.UserSeeingDirectory).Returns(userSeeing);
        workspaceMock.Setup(w => w.ProjectSeeingDirectory).Returns(projectSeeing);

        var configManager = new UnifiedConfigManager(
            workspaceMock.Object,
            NullLogger<UnifiedConfigManager>.Instance);

        await configManager.LoadAsync();

        configManager.GetSeeingAgentOptions().Acp.Backends["cursor"].Command.Should().Be("C:/project.cmd");

        Directory.Delete(tempDir, recursive: true);
    }
}

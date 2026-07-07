using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class AcpRealFileMergeTests
{
    [Fact]
    public async Task ReloadWithRealUserAndProjectFiles_ProjectBackendOverridesUser()
    {
        var userPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".seeing",
            "seeing.json");
        var projectRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "samples", "Seeing.Agent.WebUI"));
        var projectPath = Path.Combine(projectRoot, ".seeing", "seeing.json");

        if (!File.Exists(userPath) || !File.Exists(projectPath))
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), "acp-real-merge-" + Guid.NewGuid().ToString("N"));
        var tempProjectSeeing = Path.Combine(tempDir, ".seeing");
        Directory.CreateDirectory(tempProjectSeeing);
        File.Copy(projectPath, Path.Combine(tempProjectSeeing, "seeing.json"), overwrite: true);

        var projectFile = Path.Combine(tempProjectSeeing, "seeing.json");
        var json = File.ReadAllText(projectFile);
        json = json.Replace(
            @"C:\\Users\\quand\\AppData\\Local\\cursor-agent\\agent.cmd",
            @"C:\\TEMP\\project-override.cmd",
            StringComparison.Ordinal);
        json = json.Replace(
            @"C:\Users\quand\AppData\Local\cursor-agent\agent.cmd",
            @"C:\TEMP\project-override.cmd",
            StringComparison.Ordinal);
        File.WriteAllText(projectFile, json);

        // Use UnifiedConfigManager to load and merge
        var userSeeingDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".seeing");

        var workspaceMock = new Mock<IWorkspaceProvider>();
        workspaceMock.Setup(w => w.WorkspaceRoot).Returns(tempDir);
        workspaceMock.Setup(w => w.UserSeeingDirectory).Returns(userSeeingDir);
        workspaceMock.Setup(w => w.ProjectSeeingDirectory).Returns(tempProjectSeeing);

        var configManager = new UnifiedConfigManager(
            workspaceMock.Object,
            NullLogger<UnifiedConfigManager>.Instance);

        await configManager.LoadAsync();

        var options = configManager.GetSeeingAgentOptions();
        options.Acp.Backends.Should().ContainKey("cursor");
        options.Acp.Backends["cursor"].Command.Should().Be(@"C:\TEMP\project-override.cmd");

        Directory.Delete(tempDir, recursive: true);
    }
}

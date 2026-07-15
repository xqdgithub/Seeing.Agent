using Seeing.Agent.Configuration;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Microsoft.Extensions.Logging;
using Moq;

namespace Seeing.Agent.Tests.Configuration
{
    public class AgentConfigLoaderTests : IDisposable
    {
        private readonly string _testDirectory;
        private readonly string _userSeeingDir;
        private readonly string _projectSeeingDir;
        private readonly Mock<IWorkspaceProvider> _workspaceProviderMock;
        private readonly Mock<ILogger<AgentConfigLoader>> _loggerMock;
        private readonly AgentConfigLoader _loader;

        public AgentConfigLoaderTests()
        {
            _testDirectory = Path.Combine(Path.GetTempPath(), $"agent-config-tests-{Guid.NewGuid()}");
            _userSeeingDir = Path.Combine(_testDirectory, "user", ".seeing", "agents");
            _projectSeeingDir = Path.Combine(_testDirectory, "project", ".seeing", "agents");
            
            Directory.CreateDirectory(_userSeeingDir);
            Directory.CreateDirectory(_projectSeeingDir);

            _workspaceProviderMock = new Mock<IWorkspaceProvider>();
            _workspaceProviderMock.Setup(x => x.UserSeeingDirectory).Returns(Path.Combine(_testDirectory, "user", ".seeing"));
            _workspaceProviderMock.Setup(x => x.ProjectSeeingDirectory).Returns(Path.Combine(_testDirectory, "project", ".seeing"));
            
            _loggerMock = new Mock<ILogger<AgentConfigLoader>>();
            _loader = new AgentConfigLoader(_workspaceProviderMock.Object, _loggerMock.Object);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDirectory))
                Directory.Delete(_testDirectory, recursive: true);
        }

        [Fact]
        public async Task ParseFile_ValidYaml_ReturnsConfigFile()
        {
            var filePath = Path.Combine(_projectSeeingDir, "test-agent.md");
            await File.WriteAllTextAsync(filePath, """
                ---
                name: test-agent
                description: Test agent
                maxSteps: 50
                ---
                You are a test agent.
                """);

            var result = _loader.ParseFile(filePath);

            Assert.Equal("test-agent", result!.Name);
            Assert.Equal("Test agent", result.Description);
            Assert.Equal(50, result.MaxSteps);
            Assert.Equal("You are a test agent.", result.SystemPrompt?.Trim());
        }

        [Fact]
        public async Task ParseFile_WithVariants_ReturnsVariants()
        {
            var filePath = Path.Combine(_projectSeeingDir, "test-agent.md");
            await File.WriteAllTextAsync(filePath, """
                ---
                name: test-agent
                variants:
                  openai:
                    temperature: 0.7
                  anthropic:
                    temperature: 0.5
                    systemPromptAppend: |
                      Use XML tags.
                ---
                Base prompt.
                """);

            var result = _loader.ParseFile(filePath);

            Assert.NotNull(result!.Variants);
            Assert.Equal(2, result.Variants.Count);
            Assert.Equal(0.7, result.Variants["openai"].Temperature);
            Assert.Equal(0.5, result.Variants["anthropic"].Temperature);
            Assert.Contains("Use XML tags", result.Variants["anthropic"].SystemPromptAppend);
        }

        [Fact]
        public async Task ParseFile_InvalidYaml_ReturnsNull()
        {
            var filePath = Path.Combine(_projectSeeingDir, "invalid.md");
            await File.WriteAllTextAsync(filePath, """
                ---
                name: [invalid yaml
                ---
                Content
                """);

            var result = _loader.ParseFile(filePath);

            Assert.Null(result);
        }

        [Fact]
        public async Task ParseFile_MissingName_ReturnsNull()
        {
            var filePath = Path.Combine(_projectSeeingDir, "no-name.md");
            await File.WriteAllTextAsync(filePath, """
                ---
                description: No name agent
                ---
                Content
                """);

            var result = _loader.ParseFile(filePath);

            Assert.Null(result);
        }

        [Fact]
        public async Task ParseFile_NoFrontMatter_ReturnsNull()
        {
            var filePath = Path.Combine(_projectSeeingDir, "no-frontmatter.md");
            await File.WriteAllTextAsync(filePath, "Just content without front matter");

            var result = _loader.ParseFile(filePath);

            Assert.Null(result);
        }

        [Fact]
        public async Task LoadAsync_WithValidAgent_ReturnsDefinition()
        {
            var filePath = Path.Combine(_projectSeeingDir, "my-agent.md");
            await File.WriteAllTextAsync(filePath, """
                ---
                name: my-agent
                description: My test agent
                maxSteps: 100
                ---
                You are my test agent.
                """);

            var result = await _loader.LoadAsync("my-agent");

            Assert.NotNull(result);
            Assert.Equal("my-agent", result!.Name);
            Assert.Equal("My test agent", result.Description);
            Assert.Equal(100, result.MaxSteps);
            Assert.Contains("You are my test agent", result.SystemPrompt);
        }

        [Fact]
        public async Task LoadAsync_WithNonExistentAgent_ReturnsNull()
        {
            var result = await _loader.LoadAsync("non-existent-agent");

            Assert.Null(result);
        }

        [Fact]
        public async Task LoadAsync_WithNullAgentName_ThrowsArgumentNullException()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => _loader.LoadAsync(null!));
        }

        [Fact]
        public async Task LoadAsync_WithEmptyAgentName_ThrowsArgumentException()
        {
            await Assert.ThrowsAsync<ArgumentException>(() => _loader.LoadAsync(string.Empty));
        }

        [Fact]
        public async Task CreateAsync_ShouldCreateMdFile()
        {
            var config = await _loader.CreateAsync("new-agent", ConfigLevel.Project);

            Assert.NotNull(config);
            Assert.Equal("new-agent", config.Name);
            Assert.True(File.Exists(Path.Combine(_projectSeeingDir, "new-agent.md")));
        }

        [Fact]
        public async Task SaveAsync_ShouldUpdateMdFile()
        {
            // First create a file
            await _loader.CreateAsync("agent-to-update", ConfigLevel.User);

            var newContent = """
                ---
                name: agent-to-update
                description: Updated description
                maxSteps: 100
                ---
                Updated system prompt.
                """;

            var result = await _loader.SaveAsync("agent-to-update", ConfigLevel.User, newContent);

            Assert.True(result);
            var filePath = Path.Combine(_userSeeingDir, "agent-to-update.md");
            var savedContent = await File.ReadAllTextAsync(filePath);
            Assert.Contains("Updated description", savedContent);
            Assert.Contains("Updated system prompt", savedContent);
        }

        [Fact]
        public async Task DeleteAsync_ShouldRemoveMdFile()
        {
            // First create a file
            await _loader.CreateAsync("agent-to-delete", ConfigLevel.Project);
            var filePath = Path.Combine(_projectSeeingDir, "agent-to-delete.md");
            Assert.True(File.Exists(filePath));

            var result = await _loader.DeleteAsync("agent-to-delete", ConfigLevel.Project);

            Assert.True(result);
            Assert.False(File.Exists(filePath));
        }

        [Fact]
        public async Task GetAllWithLevelAsync_ShouldReturnAllMdConfigs()
        {
            // Create test files in both user and project directories
            await File.WriteAllTextAsync(Path.Combine(_userSeeingDir, "user-agent.md"), """
                ---
                name: user-agent
                description: User level agent
                ---
                User agent prompt.
                """);

            await File.WriteAllTextAsync(Path.Combine(_projectSeeingDir, "project-agent.md"), """
                ---
                name: project-agent
                description: Project level agent
                ---
                Project agent prompt.
                """);

            var result = await _loader.GetAllWithLevelAsync();

            Assert.Equal(2, result.Count);
            Assert.Contains(result, r => r.Name == "user-agent" && r.Level == ConfigLevel.User);
            Assert.Contains(result, r => r.Name == "project-agent" && r.Level == ConfigLevel.Project);
        }
    }
}

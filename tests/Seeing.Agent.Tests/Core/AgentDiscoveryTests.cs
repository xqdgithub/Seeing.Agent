using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using System.IO;
using System.Text;
using Xunit;

namespace Seeing.Agent.Tests.Discovery
{
    /// <summary>
    /// AgentDiscovery 单元测试
    /// </summary>
    public class AgentDiscoveryTests : IDisposable
    {
        private readonly Mock<ILogger<AgentDiscovery>> _loggerMock;
        private readonly AgentDiscovery _discovery;
        private readonly List<string> _tempDirectories = new();

        public AgentDiscoveryTests()
        {
            _loggerMock = new Mock<ILogger<AgentDiscovery>>();
            _discovery = new AgentDiscovery(_loggerMock.Object);
            _discovery.ClearSearchDirectories(); // 清除默认目录
        }

        public void Dispose()
        {
            // 清理临时目录
            foreach (var dir in _tempDirectories)
            {
                if (Directory.Exists(dir))
                {
                    try
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                    catch { }
                }
            }
        }

        [Fact]
        public void AddSearchDirectory_ShouldAddDirectory()
        {
            // Act
            _discovery.AddSearchDirectory("./test/path");

            // Assert
            var directories = _discovery.GetSearchDirectories();
            directories.Should().Contain(d => d.EndsWith("test" + Path.DirectorySeparatorChar + "path"));
        }

        [Fact]
        public void ClearSearchDirectories_ShouldRemoveAllDirectories()
        {
            // Arrange
            _discovery.AddSearchDirectory("./path1");
            _discovery.AddSearchDirectory("./path2");

            // Act
            _discovery.ClearSearchDirectories();

            // Assert
            _discovery.GetSearchDirectories().Should().BeEmpty();
        }

        [Fact]
        public async Task DiscoverAgentsAsync_ShouldFindMarkdownAgents()
        {
            // Arrange
            var tempDir = CreateTempDirectory();
            var agentDir = Path.Combine(tempDir, "agent");
            Directory.CreateDirectory(agentDir);

            var agentContent = @"---
name: test-agent
description: A test agent
mode: subagent
temperature: 0.5
---
This is the agent prompt content.";

            await File.WriteAllTextAsync(Path.Combine(agentDir, "test-agent.md"), agentContent);

            _discovery.ClearSearchDirectories();
            _discovery.AddSearchDirectory(agentDir);

            // Act
            var result = await _discovery.DiscoverAgentsAsync();

            // Assert
            result.Should().HaveCount(1);
            result[0].Name.Should().Be("test-agent");
            result[0].Description.Should().Be("A test agent");
            result[0].Mode.Should().Be(AgentMode.SubAgent);
            result[0].Temperature.Should().Be(0.5);
            result[0].SystemPrompt.Should().Contain("This is the agent prompt content");
        }

        [Fact]
        public async Task DiscoverAgentsAsync_ShouldUseFileNameAsName_WhenNameNotSpecified()
        {
            // Arrange
            var tempDir = CreateTempDirectory();
            var agentDir = Path.Combine(tempDir, "agent");
            Directory.CreateDirectory(agentDir);

            var agentContent = @"---
description: Agent without name
---
Prompt content.";

            await File.WriteAllTextAsync(Path.Combine(agentDir, "my-custom-agent.md"), agentContent);

            _discovery.ClearSearchDirectories();
            _discovery.AddSearchDirectory(agentDir);

            // Act
            var result = await _discovery.DiscoverAgentsAsync();

            // Assert
            result.Should().HaveCount(1);
            result[0].Name.Should().Be("my-custom-agent");
        }

        [Fact]
        public async Task DiscoverAgentsAsync_ShouldSkipFilesWithoutFrontmatter()
        {
            // Arrange
            var tempDir = CreateTempDirectory();
            var agentDir = Path.Combine(tempDir, "agent");
            Directory.CreateDirectory(agentDir);

            var agentWithoutFrontmatter = "This is just a regular markdown file without frontmatter.";
            var agentWithFrontmatter = @"---
name: valid-agent
---
Valid agent prompt.";

            await File.WriteAllTextAsync(Path.Combine(agentDir, "invalid.md"), agentWithoutFrontmatter);
            await File.WriteAllTextAsync(Path.Combine(agentDir, "valid.md"), agentWithFrontmatter);

            _discovery.ClearSearchDirectories();
            _discovery.AddSearchDirectory(agentDir);

            // Act
            var result = await _discovery.DiscoverAgentsAsync();

            // Assert
            result.Should().HaveCount(1);
            result[0].Name.Should().Be("valid-agent");
        }

        [Fact]
        public async Task DiscoverAgentsAsync_ShouldParsePermissionsFromTools()
        {
            // Arrange
            var tempDir = CreateTempDirectory();
            var agentDir = Path.Combine(tempDir, "agent");
            Directory.CreateDirectory(agentDir);

            var agentContent = @"---
name: restricted-agent
tools:
  read: true
  write: false
  bash: true
---
Agent with tool restrictions.";

            await File.WriteAllTextAsync(Path.Combine(agentDir, "restricted.md"), agentContent);

            _discovery.ClearSearchDirectories();
            _discovery.AddSearchDirectory(agentDir);

            // Act
            var result = await _discovery.DiscoverAgentsAsync();

            // Assert
            result.Should().HaveCount(1);
            var permissions = result[0].Permissions;
            permissions.Should().Contain(p => p.Permission == "read" && p.Action == PermissionAction.Allow);
            permissions.Should().Contain(p => p.Permission == "write" && p.Action == PermissionAction.Deny);
            permissions.Should().Contain(p => p.Permission == "bash" && p.Action == PermissionAction.Allow);
        }

        [Fact]
        public async Task DiscoverAgentsAsync_ShouldParsePermissionsFromPermissionField()
        {
            // Arrange
            var tempDir = CreateTempDirectory();
            var agentDir = Path.Combine(tempDir, "agent");
            Directory.CreateDirectory(agentDir);

            var agentContent = @"---
name: permission-agent
permission:
  read: allow
  edit: deny
  task: ask
---
Agent with permission config.";

            await File.WriteAllTextAsync(Path.Combine(agentDir, "permission.md"), agentContent);

            _discovery.ClearSearchDirectories();
            _discovery.AddSearchDirectory(agentDir);

            // Act
            var result = await _discovery.DiscoverAgentsAsync();

            // Assert
            result.Should().HaveCount(1);
            var permissions = result[0].Permissions;
            permissions.Should().Contain(p => p.Permission == "read" && p.Action == PermissionAction.Allow);
            permissions.Should().Contain(p => p.Permission == "edit" && p.Action == PermissionAction.Deny);
            permissions.Should().Contain(p => p.Permission == "task" && p.Action == PermissionAction.Ask);
        }

        [Fact]
        public async Task DiscoverAgentsAsync_ShouldParseModelReference()
        {
            // Arrange
            var tempDir = CreateTempDirectory();
            var agentDir = Path.Combine(tempDir, "agent");
            Directory.CreateDirectory(agentDir);

            var agentContent = @"---
name: model-agent
model: openai/gpt-4o
---
Agent with model.";

            await File.WriteAllTextAsync(Path.Combine(agentDir, "model.md"), agentContent);

            _discovery.ClearSearchDirectories();
            _discovery.AddSearchDirectory(agentDir);

            // Act
            var result = await _discovery.DiscoverAgentsAsync();

            // Assert
            result.Should().HaveCount(1);
            result[0].Model.Should().NotBeNull();
            result[0].Model!.ProviderId.Should().Be("openai");
            result[0].Model!.ModelId.Should().Be("gpt-4o");
        }

        [Fact]
        public async Task DiscoverAgentsAsync_ShouldParseTags()
        {
            // Arrange
            var tempDir = CreateTempDirectory();
            var agentDir = Path.Combine(tempDir, "agent");
            Directory.CreateDirectory(agentDir);

            var agentContent = @"---
name: tagged-agent
tags: exploration, readonly, fast
---
Agent with tags.";

            await File.WriteAllTextAsync(Path.Combine(agentDir, "tagged.md"), agentContent);

            _discovery.ClearSearchDirectories();
            _discovery.AddSearchDirectory(agentDir);

            // Act
            var result = await _discovery.DiscoverAgentsAsync();

            // Assert
            result.Should().HaveCount(1);
            result[0].Tags.Should().Contain(new[] { "exploration", "readonly", "fast" });
        }

        [Fact]
        public async Task DiscoverAgentsAsync_ShouldParseMode()
        {
            // Arrange
            var tempDir = CreateTempDirectory();
            var agentDir = Path.Combine(tempDir, "agent");
            Directory.CreateDirectory(agentDir);

            var primaryAgentContent = @"---
name: primary-agent
mode: primary
---
Primary agent.";

            var subAgentContent = @"---
name: sub-agent
mode: subagent
---
Sub agent.";

            await File.WriteAllTextAsync(Path.Combine(agentDir, "primary.md"), primaryAgentContent);
            await File.WriteAllTextAsync(Path.Combine(agentDir, "sub.md"), subAgentContent);

            _discovery.ClearSearchDirectories();
            _discovery.AddSearchDirectory(agentDir);

            // Act
            var result = await _discovery.DiscoverAgentsAsync();

            // Assert
            result.Should().HaveCount(2);
            var primaryAgent = result.FirstOrDefault(a => a.Name == "primary-agent");
            var subAgent = result.FirstOrDefault(a => a.Name == "sub-agent");
            
            primaryAgent.Should().NotBeNull();
            primaryAgent!.Mode.Should().Be(AgentMode.Primary);
            
            subAgent.Should().NotBeNull();
            subAgent!.Mode.Should().Be(AgentMode.SubAgent);
        }

        [Fact]
        public async Task DiscoverAgentsAsync_ShouldFindAgentsFromMultipleDirectories()
        {
            // Arrange
            var tempDir = CreateTempDirectory();
            var agentDir1 = Path.Combine(tempDir, "agents1");
            var agentDir2 = Path.Combine(tempDir, "agents2");
            Directory.CreateDirectory(agentDir1);
            Directory.CreateDirectory(agentDir2);

            var agent1 = @"---
name: agent-one
---
First agent.";

            var agent2 = @"---
name: agent-two
---
Second agent.";

            await File.WriteAllTextAsync(Path.Combine(agentDir1, "one.md"), agent1);
            await File.WriteAllTextAsync(Path.Combine(agentDir2, "two.md"), agent2);

            _discovery.ClearSearchDirectories();
            _discovery.AddSearchDirectory(agentDir1);
            _discovery.AddSearchDirectory(agentDir2);

            // Act
            var result = await _discovery.DiscoverAgentsAsync();

            // Assert
            result.Should().HaveCount(2);
            result.Select(a => a.Name).Should().Contain(new[] { "agent-one", "agent-two" });
        }

        private string CreateTempDirectory()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "SeeingAgentTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            _tempDirectories.Add(tempDir);
            return tempDir;
        }
    }
}
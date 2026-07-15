using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;
using System.IO;
using Xunit;

namespace Seeing.Agent.Tests.Configuration
{
    /// <summary>
    /// AgentRegistry 与 AgentConfigLoader 集成测试
    /// </summary>
    public class AgentConfigIntegrationTests : IDisposable
    {
        private readonly string _testDirectory;
        private readonly string _projectSeeingDir;
        private readonly Mock<IWorkspaceProvider> _workspaceProviderMock;
        private readonly Mock<ILogger<AgentConfigLoader>> _configLoaderLoggerMock;
        private readonly Mock<ILogger<AgentRegistry>> _registryLoggerMock;
        private readonly Mock<IAgentStore> _storeMock;
        private readonly Mock<IAgentRuntimeManager> _runtimeManagerMock;

        public AgentConfigIntegrationTests()
        {
            _testDirectory = Path.Combine(Path.GetTempPath(), $"agent-config-integration-{Guid.NewGuid()}");
            _projectSeeingDir = Path.Combine(_testDirectory, ".seeing");
            Directory.CreateDirectory(Path.Combine(_projectSeeingDir, "agents"));

            _workspaceProviderMock = new Mock<IWorkspaceProvider>();
            _workspaceProviderMock.Setup(x => x.ProjectSeeingDirectory).Returns(_projectSeeingDir);
            _workspaceProviderMock.Setup(x => x.UserSeeingDirectory).Returns(Path.Combine(_testDirectory, "user", ".seeing"));

            _configLoaderLoggerMock = new Mock<ILogger<AgentConfigLoader>>();
            _registryLoggerMock = new Mock<ILogger<AgentRegistry>>();
            _storeMock = new Mock<IAgentStore>();
            _runtimeManagerMock = new Mock<IAgentRuntimeManager>();
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDirectory))
                Directory.Delete(_testDirectory, recursive: true);
        }

        private AgentRegistry CreateRegistryWithConfigLoader(
            IEnumerable<AgentInfo> builtInAgents,
            IAgentConfigLoader? configLoader = null,
            IOptions<SeeingAgentOptions>? options = null)
        {
            var agentList = builtInAgents.ToList();

            _storeMock
                .Setup(s => s.GetAllAsync())
                .ReturnsAsync(agentList.AsReadOnly());

            foreach (var agent in agentList)
            {
                _storeMock
                    .Setup(s => s.GetAsync(agent.Name))
                    .ReturnsAsync(agent);
                _storeMock
                    .Setup(s => s.Has(agent.Name))
                    .Returns(true);
            }

            // Setup RegisterAsync to update the agent (not needed for these tests)
            _storeMock
                .Setup(s => s.RegisterAsync(It.IsAny<AgentInfo>()))
                .Returns(Task.CompletedTask);

            _runtimeManagerMock
                .Setup(r => r.GetDefaultAgentNameAsync())
                .ReturnsAsync((string?)null);

            // Create a fresh copy for the registry constructor to avoid collection modification
            var agentsForRegistry = agentList.ToList();

            return new AgentRegistry(
                _registryLoggerMock.Object,
                _storeMock.Object,
                _runtimeManagerMock.Object,
                agentsForRegistry,
                options: options,
                configLoader: configLoader);
        }

        [Fact]
        public async Task MDConfig_OverridesCodeDefinition()
        {
            // Arrange - 创建 MD 配置文件，覆盖代码定义的 SystemPrompt
            var agentMdPath = Path.Combine(_projectSeeingDir, "agents", "test-agent.md");
            await File.WriteAllTextAsync(agentMdPath, """
                ---
                name: test-agent
                description: MD description override
                maxSteps: 100
                temperature: 0.8
                ---
                You are a test agent from MD config.
                """);

            var configLoader = new AgentConfigLoader(_workspaceProviderMock.Object, _configLoaderLoggerMock.Object);

            // 创建代码定义的 Agent（带有默认值）
            var builtInAgents = new List<AgentInfo>
            {
                new AgentInfo
                {
                    Name = "test-agent",
                    Description = "Code defined description",
                    SystemPrompt = "Code defined prompt",
                    MaxSteps = 50,
                    Temperature = 0.5
                }
            };

            var registry = CreateRegistryWithConfigLoader(builtInAgents, configLoader);

            // Act - 使用合并方法获取 Agent
            var result = await registry.GetAgentWithMergedConfigAsync("test-agent");

            // Assert - MD 配置应该覆盖代码定义
            result.Should().NotBeNull();
            result!.Description.Should().Be("MD description override", "MD description should override code definition");
            result.SystemPrompt.Should().Be("You are a test agent from MD config.", "MD SystemPrompt should override code definition");
            result.MaxSteps.Should().Be(100, "MD MaxSteps should override code definition");
            result.Temperature.Should().Be(0.8, "MD Temperature should override code definition");
        }

        [Fact]
        public async Task MDConfig_WithMissingFields_KeepsCodeDefaults()
        {
            // Arrange - MD 配置只覆盖部分字段
            var agentMdPath = Path.Combine(_projectSeeingDir, "agents", "partial-agent.md");
            await File.WriteAllTextAsync(agentMdPath, """
                ---
                name: partial-agent
                temperature: 0.9
                ---
                Partial MD prompt.
                """);

            var configLoader = new AgentConfigLoader(_workspaceProviderMock.Object, _configLoaderLoggerMock.Object);

            var builtInAgents = new List<AgentInfo>
            {
                new AgentInfo
                {
                    Name = "partial-agent",
                    Description = "Original description",
                    SystemPrompt = "Original prompt",
                    MaxSteps = 50,
                    Temperature = 0.5
                }
            };

            var registry = CreateRegistryWithConfigLoader(builtInAgents, configLoader);

            // Act
            var result = await registry.GetAgentWithMergedConfigAsync("partial-agent");

            // Assert - 未覆盖的字段应保留原值
            result.Should().NotBeNull();
            result!.Description.Should().Be("Original description", "Description should keep code default when not in MD");
            result.SystemPrompt.Should().Be("Partial MD prompt.", "SystemPrompt should be overridden from MD");
            result.MaxSteps.Should().Be(50, "MaxSteps should keep code default when not in MD");
            result.Temperature.Should().Be(0.9, "Temperature should be overridden from MD");
        }

        [Fact]
        public async Task Variant_Selection_Works()
        {
            // Arrange - 创建带变体的 MD 配置
            var agentMdPath = Path.Combine(_projectSeeingDir, "agents", "variant-agent.md");
            await File.WriteAllTextAsync(agentMdPath, """
                ---
                name: variant-agent
                temperature: 0.5
                variants:
                  openai:
                    temperature: 0.7
                    systemPromptAppend: |
                      Use function calling.
                  anthropic:
                    temperature: 0.3
                    systemPromptAppend: |
                      Use XML tags.
                  openai.gpt-4o-mini:
                    temperature: 0.9
                    systemPromptAppend: |
                      Be concise.
                ---
                Base prompt for variant agent.
                """);

            var configLoader = new AgentConfigLoader(_workspaceProviderMock.Object, _configLoaderLoggerMock.Object);

            var builtInAgents = new List<AgentInfo>
            {
                new AgentInfo
                {
                    Name = "variant-agent",
                    SystemPrompt = "Base prompt for variant agent.",
                    Temperature = 0.5
                }
            };

            var registry = CreateRegistryWithConfigLoader(builtInAgents, configLoader);

            // Act & Assert - 测试 Provider 级别变体
            var openaiResult = await registry.GetAgentWithMergedConfigAsync("variant-agent", provider: "openai");
            openaiResult.Should().NotBeNull();
            openaiResult!.Temperature.Should().Be(0.7, "OpenAI variant should use provider-level temperature");
            openaiResult.SystemPrompt.Should().Contain("Use function calling", "OpenAI variant should append system prompt");

            // Act & Assert - 测试 Provider.Model 精确匹配变体
            var gpt4oMiniResult = await registry.GetAgentWithMergedConfigAsync("variant-agent", provider: "openai", model: "gpt-4o-mini");
            gpt4oMiniResult.Should().NotBeNull();
            gpt4oMiniResult!.Temperature.Should().Be(0.9, "Exact model match should use provider.model variant");
            gpt4oMiniResult.SystemPrompt.Should().Contain("Be concise", "Exact model variant should append system prompt");

            // Act & Assert - 测试另一个 Provider 级别变体
            var anthropicResult = await registry.GetAgentWithMergedConfigAsync("variant-agent", provider: "anthropic");
            anthropicResult.Should().NotBeNull();
            anthropicResult!.Temperature.Should().Be(0.3, "Anthropic variant should use provider-level temperature");
            anthropicResult.SystemPrompt.Should().Contain("Use XML tags", "Anthropic variant should append system prompt");

            // Act & Assert - 测试无匹配 Provider 时使用基础配置
            var noMatchResult = await registry.GetAgentWithMergedConfigAsync("variant-agent", provider: "other");
            noMatchResult.Should().NotBeNull();
            noMatchResult!.Temperature.Should().Be(0.5, "No matching provider should use base config");
        }

        [Fact]
        public async Task JsonConfig_AppliedAfterMdConfig()
        {
            // Arrange - 创建 MD 配置
            var agentMdPath = Path.Combine(_projectSeeingDir, "agents", "json-override-agent.md");
            await File.WriteAllTextAsync(agentMdPath, """
                ---
                name: json-override-agent
                description: MD description
                maxSteps: 100
                ---
                MD system prompt.
                """);

            var configLoader = new AgentConfigLoader(_workspaceProviderMock.Object, _configLoaderLoggerMock.Object);

            var builtInAgents = new List<AgentInfo>
            {
                new AgentInfo
                {
                    Name = "json-override-agent",
                    Description = "Code description",
                    MaxSteps = 50
                }
            };

            // 创建 seeing.json 配置
            var jsonConfig = new SeeingAgentOptions
            {
                Agents = new Dictionary<string, AgentConfig>
                {
                    ["json-override-agent"] = new AgentConfig
                    {
                        Description = "JSON description override",
                        MaxSteps = 200,
                        Temperature = 0.9
                    }
                }
            };
            var options = Options.Create(jsonConfig);

            var registry = CreateRegistryWithConfigLoader(builtInAgents, configLoader, options);

            // Act
            var result = await registry.GetAgentWithMergedConfigAsync("json-override-agent");

            // Assert - JSON 配置应该覆盖 MD 配置
            result.Should().NotBeNull();
            result!.Description.Should().Be("JSON description override", "JSON config should override MD config");
            result.MaxSteps.Should().Be(200, "JSON MaxSteps should override MD MaxSteps");
            result.Temperature.Should().Be(0.9, "JSON Temperature should be applied");
            // SystemPrompt 应该来自 MD（JSON 未设置）
            result.SystemPrompt.Should().Be("MD system prompt.", "SystemPrompt should come from MD when not in JSON");
        }

        [Fact]
        public async Task NoConfigLoader_ReturnsBaseAgentInfo()
        {
            // Arrange - 不提供 ConfigLoader
            var builtInAgents = new List<AgentInfo>
            {
                new AgentInfo
                {
                    Name = "no-loader-agent",
                    Description = "Base description",
                    SystemPrompt = "Base prompt",
                    MaxSteps = 50
                }
            };

            var registry = CreateRegistryWithConfigLoader(builtInAgents, configLoader: null);

            // Act
            var result = await registry.GetAgentWithMergedConfigAsync("no-loader-agent");

            // Assert - 应返回原始 AgentInfo
            result.Should().NotBeNull();
            result!.Description.Should().Be("Base description");
            result.SystemPrompt.Should().Be("Base prompt");
            result.MaxSteps.Should().Be(50);
        }

        [Fact]
        public async Task NonExistentAgent_ReturnsNull()
        {
            // Arrange
            var configLoader = new AgentConfigLoader(_workspaceProviderMock.Object, _configLoaderLoggerMock.Object);
            var registry = CreateRegistryWithConfigLoader(Enumerable.Empty<AgentInfo>(), configLoader);

            // Act
            var result = await registry.GetAgentWithMergedConfigAsync("non-existent-agent");

            // Assert
            result.Should().BeNull();
        }
    }
}
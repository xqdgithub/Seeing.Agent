using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Rules;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Configuration
{
    /// <summary>
    /// ConfigurationPersistence 单元测试
    /// </summary>
    public class ConfigurationPersistenceTests : IDisposable
    {
        private readonly Mock<ILogger<ConfigurationPersistence>> _loggerMock;
        private readonly string _testDirectory;

        public ConfigurationPersistenceTests()
        {
            _loggerMock = new Mock<ILogger<ConfigurationPersistence>>();
            _testDirectory = Path.Combine(Path.GetTempPath(), $"seeing_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_testDirectory);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, true);
            }
        }

        [Fact]
        public async Task LoadAsync_WhenFileNotExists_ReturnsDefaultSettings()
        {
            // Arrange
            var persistence = new ConfigurationPersistence(_loggerMock.Object, _testDirectory);

            // Act
            var result = await persistence.LoadAsync();

            // Assert
            result.Should().NotBeNull();
            result.DefaultAgent.Should().BeNull();
            result.DefaultModel.Should().BeNull();
            result.AgentModels.Should().BeEmpty();
        }

        [Fact]
        public async Task SaveAsync_CreatesFileAndSavesSettings()
        {
            // Arrange
            var persistence = new ConfigurationPersistence(_loggerMock.Object, _testDirectory);
            var settings = new RuntimeSettings
            {
                DefaultAgent = "build",
                DefaultModel = "openai/gpt-4"
            };

            // Act
            await persistence.SaveAsync(settings);

            // Assert
            File.Exists(persistence.SettingsFilePath).Should().BeTrue();
        }

        [Fact]
        public async Task LoadAsync_AfterSave_ReturnsSavedSettings()
        {
            // Arrange
            var persistence = new ConfigurationPersistence(_loggerMock.Object, _testDirectory);
            var settings = new RuntimeSettings
            {
                DefaultAgent = "plan",
                DefaultModel = "anthropic/claude-3",
                AgentModels = new Dictionary<string, string>
                {
                    ["explore"] = "openai/gpt-4o-mini"
                }
            };

            // Act
            await persistence.SaveAsync(settings);
            var result = await persistence.LoadAsync();

            // Assert
            result.DefaultAgent.Should().Be("plan");
            result.DefaultModel.Should().Be("anthropic/claude-3");
            result.AgentModels.Should().ContainKey("explore");
            result.AgentModels["explore"].Should().Be("openai/gpt-4o-mini");
        }

        [Fact]
        public async Task ResetAsync_DeletesFileAndCreatesDefault()
        {
            // Arrange
            var persistence = new ConfigurationPersistence(_loggerMock.Object, _testDirectory);
            var settings = new RuntimeSettings { DefaultAgent = "test" };
            await persistence.SaveAsync(settings);

            // Act
            await persistence.ResetAsync();

            // Assert
            var result = await persistence.LoadAsync();
            result.DefaultAgent.Should().BeNull();
        }

        [Fact]
        public void RuntimeSettings_GetAgentModel_ReturnsCorrectValue()
        {
            // Arrange
            var settings = new RuntimeSettings();
            settings.SetAgentModel("build", "gpt-4");

            // Act
            var result = settings.GetAgentModel("build");

            // Assert
            result.Should().Be("gpt-4");
        }

        [Fact]
        public void RuntimeSettings_ClearAgentModel_RemovesValue()
        {
            // Arrange
            var settings = new RuntimeSettings();
            settings.SetAgentModel("build", "gpt-4");

            // Act
            settings.ClearAgentModel("build");
            var result = settings.GetAgentModel("build");

            // Assert
            result.Should().BeNull();
        }
    }

    /// <summary>
    /// AgentRegistry 持久化功能测试
    /// </summary>
    public class AgentRegistryPersistenceTests : IDisposable
    {
        private readonly Mock<ILogger<AgentRegistry>> _loggerMock;
        private readonly Mock<ILogger<RuleEngine>> _ruleEngineLoggerMock;
        private readonly Mock<ILogger<ConfigurationPersistence>> _persistenceLoggerMock;
        private readonly Mock<ILogger<AgentStore>> _storeLoggerMock;
        private readonly Mock<ILogger<AgentRuntimeManager>> _runtimeManagerLoggerMock;
        private readonly RuleEngine _ruleEngine;
        private readonly string _testDirectory;

        public AgentRegistryPersistenceTests()
        {
            _loggerMock = new Mock<ILogger<AgentRegistry>>();
            _ruleEngineLoggerMock = new Mock<ILogger<RuleEngine>>();
            _persistenceLoggerMock = new Mock<ILogger<ConfigurationPersistence>>();
            _storeLoggerMock = new Mock<ILogger<AgentStore>>();
            _runtimeManagerLoggerMock = new Mock<ILogger<AgentRuntimeManager>>();
            _ruleEngine = new RuleEngine(_ruleEngineLoggerMock.Object);
            _testDirectory = Path.Combine(Path.GetTempPath(), $"seeing_registry_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_testDirectory);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, true);
            }
        }

        private (AgentStore store, AgentRuntimeManager runtimeManager) CreateDependencies(
            IEnumerable<AgentInfo> agents, 
            IConfigurationPersistence? persistence = null)
        {
            var agentList = agents.ToList();
            
            // 创建 Store 并预注册代理
            var store = new AgentStore(_storeLoggerMock.Object);
            foreach (var agent in agentList)
            {
                store.RegisterAsync(agent).Wait();
            }

            // 创建 RuntimeManager
            var runtimeManager = new AgentRuntimeManager(
                _runtimeManagerLoggerMock.Object,
                store,
                options: null,
                persistence);

            return (store, runtimeManager);
        }

        [Fact]
        public async Task SetDefaultAgentAsync_UpdatesAndPersistsSettings()
        {
            // Arrange
            var persistence = new ConfigurationPersistence(_persistenceLoggerMock.Object, _testDirectory);
            var agents = new List<AgentInfo>
            {
                new AgentInfo { Name = "build", Mode = AgentMode.Primary, IsHidden = false },
                new AgentInfo { Name = "plan", Mode = AgentMode.Primary, IsHidden = false }
            };
            var (store, runtimeManager) = CreateDependencies(agents, persistence);
            var registry = new AgentRegistry(_loggerMock.Object, _ruleEngine, store, runtimeManager, agents);
            await runtimeManager.InitializeAsync();

            // Act
            await registry.SetDefaultAgentAsync("plan");
            var result = await registry.GetDefaultAgentNameAsync();

            // Assert
            result.Should().Be("plan");

            // 验证持久化
            var loadedSettings = await persistence.LoadAsync();
            loadedSettings.DefaultAgent.Should().Be("plan");
        }

        [Fact]
        public async Task SetDefaultAgentAsync_ThrowsForNonExistentAgent()
        {
            // Arrange
            var persistence = new ConfigurationPersistence(_persistenceLoggerMock.Object, _testDirectory);
            var agents = new List<AgentInfo>
            {
                new AgentInfo { Name = "build", Mode = AgentMode.Primary }
            };
            var (store, runtimeManager) = CreateDependencies(agents, persistence);
            var registry = new AgentRegistry(_loggerMock.Object, _ruleEngine, store, runtimeManager, agents);
            await runtimeManager.InitializeAsync();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => registry.SetDefaultAgentAsync("nonexistent"));
        }

        [Fact]
        public async Task UpdateAgentModelAsync_UpdatesAndPersistsSettings()
        {
            // Arrange
            var persistence = new ConfigurationPersistence(_persistenceLoggerMock.Object, _testDirectory);
            var agents = new List<AgentInfo>
            {
                new AgentInfo { Name = "build", Mode = AgentMode.Primary }
            };
            var (store, runtimeManager) = CreateDependencies(agents, persistence);
            var registry = new AgentRegistry(_loggerMock.Object, _ruleEngine, store, runtimeManager, agents);
            await runtimeManager.InitializeAsync();

            // Act
            await registry.UpdateAgentModelAsync("build", new ModelReference
            {
                ProviderId = "openai",
                ModelId = "gpt-4o"
            });
            var result = await registry.GetEffectiveModelAsync("build");

            // Assert
            result.Should().NotBeNull();
            result!.ProviderId.Should().Be("openai");
            result.ModelId.Should().Be("gpt-4o");

            // 验证持久化
            var loadedSettings = await persistence.LoadAsync();
            loadedSettings.AgentModels.Should().ContainKey("build");
            loadedSettings.AgentModels["build"].Should().Be("openai/gpt-4o");
        }

        [Fact]
        public async Task GetDefaultAgentNameAsync_PrioritizesRuntimeSettings()
        {
            // Arrange - 创建持久化设置
            var persistence = new ConfigurationPersistence(_persistenceLoggerMock.Object, _testDirectory);
            var settings = new RuntimeSettings { DefaultAgent = "plan" };
            await persistence.SaveAsync(settings);

            // 创建注册表，配置默认为 build
            var agents = new List<AgentInfo>
            {
                new AgentInfo { Name = "build", Mode = AgentMode.Primary, IsHidden = false },
                new AgentInfo { Name = "plan", Mode = AgentMode.Primary, IsHidden = false }
            };
            var (store, runtimeManager) = CreateDependencies(agents, persistence);
            var registry = new AgentRegistry(_loggerMock.Object, _ruleEngine, store, runtimeManager, agents, "build");
            await runtimeManager.InitializeAsync();

            // Act
            var result = await registry.GetDefaultAgentNameAsync();

            // Assert - 运行时设置优先
            result.Should().Be("plan");
        }

        [Fact]
        public async Task InitializeAsync_AppliesPersistedAgentModels()
        {
            // Arrange - 创建持久化设置
            var persistence = new ConfigurationPersistence(_persistenceLoggerMock.Object, _testDirectory);
            var settings = new RuntimeSettings();
            settings.SetAgentModel("explore", "anthropic/claude-3");
            await persistence.SaveAsync(settings);

            // 创建注册表
            var agents = new List<AgentInfo>
            {
                new AgentInfo { Name = "explore", Mode = AgentMode.SubAgent }
            };
            var (store, runtimeManager) = CreateDependencies(agents, persistence);
            var registry = new AgentRegistry(_loggerMock.Object, _ruleEngine, store, runtimeManager, agents);
            await runtimeManager.InitializeAsync();

            // Act
            var model = await registry.GetEffectiveModelAsync("explore");

            // Assert
            model.Should().NotBeNull();
            model!.ProviderId.Should().Be("anthropic");
            model.ModelId.Should().Be("claude-3");
        }
    }
}
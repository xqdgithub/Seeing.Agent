using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Rules;
using Xunit;

namespace Seeing.Agent.Tests.Registry
{
    /// <summary>
    /// AgentRegistry 单元测试
    /// </summary>
    public class AgentRegistryTests
    {
        private readonly Mock<ILogger<AgentRegistry>> _loggerMock;
        private readonly Mock<IRuleEngine> _ruleEngineMock;
        private readonly Mock<IAgentStore> _storeMock;
        private readonly Mock<IAgentRuntimeManager> _runtimeManagerMock;

        public AgentRegistryTests()
        {
            _loggerMock = new Mock<ILogger<AgentRegistry>>();
            _ruleEngineMock = new Mock<IRuleEngine>();
            _storeMock = new Mock<IAgentStore>();
            _runtimeManagerMock = new Mock<IAgentRuntimeManager>();
        }

        private AgentRegistry CreateRegistry(IEnumerable<AgentInfo> agents, string? defaultAgent = null)
        {
            var agentList = agents.ToList();
            
            // 设置 Store 的默认行为
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

            // 设置 RuntimeManager 的默认行为
            _runtimeManagerMock
                .Setup(r => r.GetDefaultAgentNameAsync())
                .ReturnsAsync((string?)null);

            return new AgentRegistry(
                _loggerMock.Object,
                _ruleEngineMock.Object,
                _storeMock.Object,
                _runtimeManagerMock.Object,
                agentList,
                defaultAgent);
        }

        [Fact]
        public async Task GetAgentsAsync_ShouldReturnAllRegisteredAgents()
        {
            // Arrange
            var agents = new List<AgentInfo>
            {
                new AgentInfo { Name = "test1", Mode = AgentMode.Primary },
                new AgentInfo { Name = "test2", Mode = AgentMode.SubAgent }
            };

            var registry = CreateRegistry(agents);

            // Act
            var result = await registry.GetAgentsAsync();

            // Assert
            result.Should().HaveCount(2);
            result.Select(a => a.Name).Should().Contain(new[] { "test1", "test2" });
        }

        [Fact]
        public async Task GetAgentAsync_ShouldReturnCorrectAgent()
        {
            // Arrange
            var agents = new List<AgentInfo>
            {
                new AgentInfo { Name = "build", Description = "Build agent" },
                new AgentInfo { Name = "explore", Description = "Explore agent" }
            };

            var registry = CreateRegistry(agents);

            // Act
            var result = await registry.GetAgentAsync("build");

            // Assert
            result.Should().NotBeNull();
            result!.Name.Should().Be("build");
            result.Description.Should().Be("Build agent");
        }

        [Fact]
        public async Task GetAgentAsync_ShouldReturnNull_WhenAgentNotFound()
        {
            // Arrange
            var registry = CreateRegistry(Enumerable.Empty<AgentInfo>());

            // Act
            var result = await registry.GetAgentAsync("nonexistent");

            // Assert
            result.Should().BeNull();
        }

        [Fact]
        public async Task GetSubAgentsAsync_ShouldReturnOnlySubAgents()
        {
            // Arrange
            var agents = new List<AgentInfo>
            {
                new AgentInfo { Name = "build", Mode = AgentMode.Primary },
                new AgentInfo { Name = "explore", Mode = AgentMode.SubAgent },
                new AgentInfo { Name = "general", Mode = AgentMode.SubAgent },
                new AgentInfo { Name = "universal", Mode = AgentMode.All }
            };

            var registry = CreateRegistry(agents);

            // Act
            var result = await registry.GetSubAgentsAsync();

            // Assert
            result.Should().HaveCount(2);
            result.Select(a => a.Name).Should().Contain(new[] { "explore", "general" });
        }

        [Fact]
        public async Task GetPrimaryAgentsAsync_ShouldReturnOnlyVisiblePrimaryAgents()
        {
            // Arrange
            var agents = new List<AgentInfo>
            {
                new AgentInfo { Name = "build", Mode = AgentMode.Primary, IsHidden = false },
                new AgentInfo { Name = "plan", Mode = AgentMode.Primary, IsHidden = false },
                new AgentInfo { Name = "hidden", Mode = AgentMode.Primary, IsHidden = true },
                new AgentInfo { Name = "explore", Mode = AgentMode.SubAgent }
            };

            var registry = CreateRegistry(agents);

            // Act
            var result = await registry.GetPrimaryAgentsAsync();

            // Assert
            result.Should().HaveCount(2);
            result.Select(a => a.Name).Should().Contain(new[] { "build", "plan" });
        }

        [Fact]
        public async Task GetPrimaryAgentsAsync_ShouldIncludeAgentModeAll()
        {
            // Arrange - AgentMode.All 模式的代理也应该出现在主代理列表中
            var agents = new List<AgentInfo>
            {
                new AgentInfo { Name = "build", Mode = AgentMode.Primary, IsHidden = false },
                new AgentInfo { Name = "universal", Mode = AgentMode.All, IsHidden = false },
                new AgentInfo { Name = "hidden_all", Mode = AgentMode.All, IsHidden = true },
                new AgentInfo { Name = "explore", Mode = AgentMode.SubAgent }
            };

            var registry = CreateRegistry(agents);

            // Act
            var result = await registry.GetPrimaryAgentsAsync();

            // Assert - AgentMode.All 的代理也应该出现在主代理列表中（但不包括隐藏的）
            result.Should().HaveCount(2);
            result.Select(a => a.Name).Should().Contain(new[] { "build", "universal" });
        }

        [Fact]
        public async Task GetPrimaryAgentsAsync_ShouldReturnAlphabeticallySorted()
        {
            // Arrange - 按不同顺序添加
            var agents = new List<AgentInfo>
            {
                new AgentInfo { Name = "zebra", Mode = AgentMode.Primary, IsHidden = false },
                new AgentInfo { Name = "alpha", Mode = AgentMode.Primary, IsHidden = false },
                new AgentInfo { Name = "middle", Mode = AgentMode.Primary, IsHidden = false }
            };

            var registry = CreateRegistry(agents);

            // Act
            var result = await registry.GetPrimaryAgentsAsync();

            // Assert - 应按字母排序
            result.Should().HaveCount(3);
            result[0].Name.Should().Be("alpha");
            result[1].Name.Should().Be("middle");
            result[2].Name.Should().Be("zebra");
        }

        [Fact]
        public async Task GetDefaultAgentNameAsync_ShouldReturnFirstVisiblePrimaryAgent()
        {
            // Arrange
            var agents = new List<AgentInfo>
            {
                new AgentInfo { Name = "build", Mode = AgentMode.Primary, IsHidden = false },
                new AgentInfo { Name = "plan", Mode = AgentMode.Primary, IsHidden = false }
            };

            var registry = CreateRegistry(agents);

            // Act
            var result = await registry.GetDefaultAgentNameAsync();
            var primaryAgents = await registry.GetPrimaryAgentsAsync();

            // Assert - 应返回按字母排序的第一个可见主代理
            result.Should().BeOneOf("build", "plan");
            result.Should().Be(primaryAgents[0].Name);
        }

        [Fact]
        public async Task GetDefaultAgentNameAsync_ShouldReturnAlphabeticallyFirstAgent()
        {
            // Arrange - 按不同顺序添加，验证排序
            var agents = new List<AgentInfo>
            {
                new AgentInfo { Name = "zebra", Mode = AgentMode.Primary, IsHidden = false },
                new AgentInfo { Name = "alpha", Mode = AgentMode.Primary, IsHidden = false }
            };

            var registry = CreateRegistry(agents);

            // Act
            var result = await registry.GetDefaultAgentNameAsync();

            // Assert - 应返回字母顺序第一个 ("alpha")
            result.Should().Be("alpha");
        }

        [Fact]
        public async Task GetDefaultAgentNameAsync_ShouldAcceptAgentModeAll()
        {
            // Arrange - 只有 AgentMode.All 模式的代理
            var agents = new List<AgentInfo>
            {
                new AgentInfo { Name = "universal", Mode = AgentMode.All, IsHidden = false },
                new AgentInfo { Name = "hidden_all", Mode = AgentMode.All, IsHidden = true }
            };

            var registry = CreateRegistry(agents);

            // Act
            var result = await registry.GetDefaultAgentNameAsync();

            // Assert - AgentMode.All 模式的代理可以作为默认代理（不包括隐藏的）
            result.Should().Be("universal");
        }

        [Fact]
        public async Task RegisterAgentAsync_ShouldAddNewAgent()
        {
            // Arrange
            var registry = CreateRegistry(Enumerable.Empty<AgentInfo>());
            var newAgent = new AgentInfo { Name = "newAgent", Mode = AgentMode.All };

            // 设置 Store 的 RegisterAsync 方法
            _storeMock
                .Setup(s => s.RegisterAsync(It.IsAny<AgentInfo>()))
                .Returns(Task.CompletedTask);

            // Act
            await registry.RegisterAgentAsync(newAgent);

            // Assert - 验证调用了 Store 的 RegisterAsync
            _storeMock.Verify(s => s.RegisterAsync(newAgent), Times.Once);
        }

        [Fact]
        public void UnregisterAgent_ShouldRemoveAgent()
        {
            // Arrange
            var agents = new List<AgentInfo>
            {
                new AgentInfo { Name = "test" }
            };

            var registry = CreateRegistry(agents);
            _storeMock.Setup(s => s.Unregister("test")).Returns(true);

            // Act
            var result = registry.UnregisterAgent("test");

            // Assert
            result.Should().BeTrue();
            _storeMock.Verify(s => s.Unregister("test"), Times.Once);
        }

        [Fact]
        public void HasAgent_ShouldReturnCorrectValue()
        {
            // Arrange
            var agents = new List<AgentInfo>
            {
                new AgentInfo { Name = "existing" }
            };

            var registry = CreateRegistry(agents);

            // Act & Assert - Store 的 Has 方法已在 CreateRegistry 中设置
            registry.HasAgent("existing").Should().BeTrue();
            registry.HasAgent("nonexistent").Should().BeFalse();
        }

        [Fact]
        public void GetOrCreateAgentInstance_ShouldReturnAgentFromFactory()
        {
            // Arrange
            var mockAgent = new Mock<IAgent>();
            var agents = new List<AgentInfo>
            {
                new AgentInfo
                {
                    Name = "factory",
                    AgentFactory = () => mockAgent.Object
                }
            };

            var registry = CreateRegistry(agents);

            // Act
            var result = registry.GetOrCreateAgentInstance("factory");

            // Assert
            result.Should().NotBeNull();
            result.Should().Be(mockAgent.Object);
        }

        [Fact]
        public async Task GetAccessibleSubAgentsAsync_ShouldFilterByPermissions()
        {
            // Arrange
            var agents = new List<AgentInfo>
            {
                new AgentInfo
                {
                    Name = "allowed",
                    Mode = AgentMode.SubAgent,
                    Permissions = new List<PermissionRule>()
                },
                new AgentInfo
                {
                    Name = "denied",
                    Mode = AgentMode.SubAgent,
                    Permissions = new List<PermissionRule>()
                }
            };

            var registry = CreateRegistry(agents);

            var callerPermissions = new List<PermissionRule>
            {
                new PermissionRule { Permission = "task", Pattern = "denied", Action = PermissionAction.Deny }
            };

            // Act
            var result = await registry.GetAccessibleSubAgentsAsync(callerPermissions);

            // Assert
            result.Should().HaveCount(1);
            result[0].Name.Should().Be("allowed");
        }

        [Fact]
        public async Task SetDefaultAgentAsync_ShouldAcceptAgentModeAll()
        {
            // Arrange
            var agents = new List<AgentInfo>
            {
                new AgentInfo { Name = "universal", Mode = AgentMode.All, IsHidden = false }
            };

            var registry = CreateRegistry(agents);

            // 设置 RuntimeManager 的 SetDefaultAgentAsync 方法
            _runtimeManagerMock
                .Setup(r => r.SetDefaultAgentAsync("universal"))
                .Returns(Task.CompletedTask);

            // Act & Assert - AgentMode.All 模式的代理可以作为默认代理
            await registry.SetDefaultAgentAsync("universal");
            _runtimeManagerMock.Verify(r => r.SetDefaultAgentAsync("universal"), Times.Once);
        }

        [Fact]
        public async Task SetDefaultAgentAsync_ShouldRejectSubAgent()
        {
            // Arrange
            var agents = new List<AgentInfo>
            {
                new AgentInfo { Name = "explore", Mode = AgentMode.SubAgent }
            };

            var registry = CreateRegistry(agents);

            // 设置 RuntimeManager 的 SetDefaultAgentAsync 方法抛出异常
            _runtimeManagerMock
                .Setup(r => r.SetDefaultAgentAsync("explore"))
                .ThrowsAsync(new ArgumentException("Agent 不是可见的主代理: explore"));

            // Act & Assert - SubAgent 模式的代理不能作为默认代理
            var act = () => registry.SetDefaultAgentAsync("explore");
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*不是可见的主代理*");
        }
    }
}
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Core.Background;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Background
{
    /// <summary>
    /// BackgroundTaskManager 单元测试
    /// </summary>
    public class BackgroundTaskManagerTests
    {
        private readonly Mock<IAgentRegistry> _agentRegistryMock;
        private readonly Mock<ILogger<BackgroundTaskManager>> _loggerMock;
        private readonly BackgroundTaskManager _manager;

        public BackgroundTaskManagerTests()
        {
            _agentRegistryMock = new Mock<IAgentRegistry>();
            _loggerMock = new Mock<ILogger<BackgroundTaskManager>>();
            _manager = new BackgroundTaskManager(_agentRegistryMock.Object, _loggerMock.Object);
        }

        /// <summary>
        /// 创建模拟的 Agent 执行序列
        /// </summary>
        private static async IAsyncEnumerable<ChatMessage> CreateAgentResponse(
            string content,
            int delayMs = 100,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Delay(delayMs, ct);
            yield return new ChatMessage { Role = ChatRole.Assistant, Content = content };
        }

        /// <summary>
        /// 创建长时间运行的 Agent 执行序列（可取消）
        /// </summary>
        private static async IAsyncEnumerable<ChatMessage> CreateLongRunningAgentResponse(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Delay(10000, ct);
            yield return new ChatMessage { Role = ChatRole.Assistant, Content = "Test output" };
        }

        [Fact]
        public async Task StartAsync_ShouldCreateTaskWithPendingStatus()
        {
            // Arrange
            var agentMock = new Mock<IAgent>();
            agentMock.Setup(a => a.Name).Returns("test-agent");
            agentMock.Setup(a => a.ExecuteAsync(It.IsAny<ChatMessage>(), It.IsAny<AgentContext>(), It.IsAny<CancellationToken>()))
                .Returns((ChatMessage input, AgentContext ctx, CancellationToken ct) => 
                    CreateAgentResponse("Test output", 100, ct));

            _agentRegistryMock.Setup(r => r.GetOrCreateAgentInstance("test-agent"))
                .Returns(agentMock.Object);

            var args = new BackgroundTaskLaunchArgs
            {
                AgentName = "test-agent",
                Input = new ChatMessage { Role = ChatRole.User, Content = "Hello" },
                Context = new AgentContext { SessionId = "test-session", MessageId = "test-msg" }
            };

            // Act
            var taskId = await _manager.StartAsync(args);

            // Assert
            taskId.Should().NotBeNullOrEmpty();
            taskId.Should().StartWith("bg_");

            var task = await _manager.GetAsync(taskId);
            task.Should().NotBeNull();
            task!.AgentName.Should().Be("test-agent");
            task.Status.Should().BeOneOf(BackgroundTaskStatus.Pending, BackgroundTaskStatus.Running);
        }

        [Fact]
        public async Task GetAsync_ShouldReturnNull_WhenTaskNotExists()
        {
            // Act
            var task = await _manager.GetAsync("nonexistent-task");

            // Assert
            task.Should().BeNull();
        }

        [Fact]
        public async Task CancelAsync_ShouldReturnFalse_WhenTaskNotExists()
        {
            // Act
            var result = await _manager.CancelAsync("nonexistent-task");

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public async Task CancelAsync_ShouldCancelRunningTask()
        {
            // Arrange
            var agentMock = new Mock<IAgent>();
            agentMock.Setup(a => a.Name).Returns("test-agent");
            agentMock.Setup(a => a.ExecuteAsync(It.IsAny<ChatMessage>(), It.IsAny<AgentContext>(), It.IsAny<CancellationToken>()))
                .Returns((ChatMessage input, AgentContext ctx, CancellationToken ct) => 
                    CreateLongRunningAgentResponse(ct));

            _agentRegistryMock.Setup(r => r.GetOrCreateAgentInstance("test-agent"))
                .Returns(agentMock.Object);

            var args = new BackgroundTaskLaunchArgs
            {
                AgentName = "test-agent",
                Input = new ChatMessage { Role = ChatRole.User, Content = "Hello" },
                Context = new AgentContext { SessionId = "test-session", MessageId = "test-msg" }
            };

            var taskId = await _manager.StartAsync(args);

            // 等待任务开始执行
            await Task.Delay(200);

            // Act
            var result = await _manager.CancelAsync(taskId);

            // Assert
            result.Should().BeTrue();

            // 等待取消生效
            await Task.Delay(200);

            var task = await _manager.GetAsync(taskId);
            task!.Status.Should().Be(BackgroundTaskStatus.Cancelled);
        }

        [Fact]
        public async Task ListAsync_ShouldReturnAllTasks()
        {
            // Arrange
            var agentMock = new Mock<IAgent>();
            agentMock.Setup(a => a.Name).Returns("test-agent");
            agentMock.Setup(a => a.ExecuteAsync(It.IsAny<ChatMessage>(), It.IsAny<AgentContext>(), It.IsAny<CancellationToken>()))
                .Returns((ChatMessage input, AgentContext ctx, CancellationToken ct) => 
                    CreateAgentResponse("Test output", 100, ct));

            _agentRegistryMock.Setup(r => r.GetOrCreateAgentInstance("test-agent"))
                .Returns(agentMock.Object);

            var args = new BackgroundTaskLaunchArgs
            {
                AgentName = "test-agent",
                Input = new ChatMessage { Role = ChatRole.User, Content = "Hello" },
                Context = new AgentContext { SessionId = "test-session", MessageId = "test-msg" }
            };

            // 启动多个任务
            await _manager.StartAsync(args);
            await _manager.StartAsync(args);

            // Act
            var tasks = await _manager.ListAsync();

            // Assert
            tasks.Should().HaveCount(2);
        }

        [Fact]
        public async Task ListAsync_ShouldFilterByStatus()
        {
            // Arrange
            var agentMock = new Mock<IAgent>();
            agentMock.Setup(a => a.Name).Returns("test-agent");
            agentMock.Setup(a => a.ExecuteAsync(It.IsAny<ChatMessage>(), It.IsAny<AgentContext>(), It.IsAny<CancellationToken>()))
                .Returns((ChatMessage input, AgentContext ctx, CancellationToken ct) => 
                    CreateAgentResponse("Test output", 100, ct));

            _agentRegistryMock.Setup(r => r.GetOrCreateAgentInstance("test-agent"))
                .Returns(agentMock.Object);

            var args = new BackgroundTaskLaunchArgs
            {
                AgentName = "test-agent",
                Input = new ChatMessage { Role = ChatRole.User, Content = "Hello" },
                Context = new AgentContext { SessionId = "test-session", MessageId = "test-msg" }
            };

            var taskId = await _manager.StartAsync(args);

            // 等待任务完成
            await Task.Delay(500);

            // Act
            var completedTasks = await _manager.ListAsync(BackgroundTaskStatus.Completed);

            // Assert
            completedTasks.Should().Contain(t => t.Id == taskId && t.Status == BackgroundTaskStatus.Completed);
        }

        [Fact]
        public async Task WaitAsync_ShouldReturnCompletedTask()
        {
            // Arrange
            var agentMock = new Mock<IAgent>();
            agentMock.Setup(a => a.Name).Returns("test-agent");
            agentMock.Setup(a => a.ExecuteAsync(It.IsAny<ChatMessage>(), It.IsAny<AgentContext>(), It.IsAny<CancellationToken>()))
                .Returns((ChatMessage input, AgentContext ctx, CancellationToken ct) => 
                    CreateAgentResponse("Test output", 0, ct));

            _agentRegistryMock.Setup(r => r.GetOrCreateAgentInstance("test-agent"))
                .Returns(agentMock.Object);

            var args = new BackgroundTaskLaunchArgs
            {
                AgentName = "test-agent",
                Input = new ChatMessage { Role = ChatRole.User, Content = "Hello" },
                Context = new AgentContext { SessionId = "test-session", MessageId = "test-msg" }
            };

            var taskId = await _manager.StartAsync(args);

            // Act
            var task = await _manager.WaitAsync(taskId, 5000);

            // Assert
            task.Should().NotBeNull();
            task!.Status.Should().Be(BackgroundTaskStatus.Completed);
            task.Result.Should().Contain("Test output");
        }

        [Fact]
        public async Task CancelAllAsync_ShouldCancelAllRunningTasks()
        {
            // Arrange
            var agentMock = new Mock<IAgent>();
            agentMock.Setup(a => a.Name).Returns("test-agent");
            agentMock.Setup(a => a.ExecuteAsync(It.IsAny<ChatMessage>(), It.IsAny<AgentContext>(), It.IsAny<CancellationToken>()))
                .Returns((ChatMessage input, AgentContext ctx, CancellationToken ct) => 
                    CreateLongRunningAgentResponse(ct));

            _agentRegistryMock.Setup(r => r.GetOrCreateAgentInstance("test-agent"))
                .Returns(agentMock.Object);

            var args = new BackgroundTaskLaunchArgs
            {
                AgentName = "test-agent",
                Input = new ChatMessage { Role = ChatRole.User, Content = "Hello" },
                Context = new AgentContext { SessionId = "test-session", MessageId = "test-msg" }
            };

            // 启动多个任务
            await _manager.StartAsync(args);
            await _manager.StartAsync(args);

            // 等待任务开始执行
            await Task.Delay(200);

            // Act
            var count = await _manager.CancelAllAsync();

            // Assert
            count.Should().Be(2);

            // 等待取消生效
            await Task.Delay(200);

            var tasks = await _manager.ListAsync();
            tasks.Should().AllSatisfy(t => t.Status.Should().Be(BackgroundTaskStatus.Cancelled));
        }

        [Fact]
        public async Task GetOutputAsync_ShouldReturnResult_WhenTaskCompleted()
        {
            // Arrange
            var agentMock = new Mock<IAgent>();
            agentMock.Setup(a => a.Name).Returns("test-agent");
            agentMock.Setup(a => a.ExecuteAsync(It.IsAny<ChatMessage>(), It.IsAny<AgentContext>(), It.IsAny<CancellationToken>()))
                .Returns((ChatMessage input, AgentContext ctx, CancellationToken ct) => 
                    CreateAgentResponse("Hello from agent", 0, ct));

            _agentRegistryMock.Setup(r => r.GetOrCreateAgentInstance("test-agent"))
                .Returns(agentMock.Object);

            var args = new BackgroundTaskLaunchArgs
            {
                AgentName = "test-agent",
                Input = new ChatMessage { Role = ChatRole.User, Content = "Hello" },
                Context = new AgentContext { SessionId = "test-session", MessageId = "test-msg" }
            };

            var taskId = await _manager.StartAsync(args);
            await _manager.WaitAsync(taskId, 5000);

            // Act
            var output = await _manager.GetOutputAsync(taskId);

            // Assert
            output.Should().NotBeNull();
            output.Should().Contain("Hello from agent");
        }
    }
}
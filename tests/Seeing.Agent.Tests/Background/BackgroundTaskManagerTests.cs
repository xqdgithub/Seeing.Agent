using Seeing.Agent.Abstractions.Agents;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Core.Background;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Events;
using System.Runtime.CompilerServices;
using Xunit;

namespace Seeing.Agent.Tests.Background
{
    /// <summary>
    /// BackgroundTaskManager 单元测试
    /// </summary>
    public class BackgroundTaskManagerTests
    {
        private readonly Mock<IAgentRegistry> _agentRegistryMock;
        private readonly Mock<IAgentExecutor> _executorMock;
        private readonly Mock<ILogger<BackgroundTaskManager>> _loggerMock;
        private readonly BackgroundTaskManager _manager;

        public BackgroundTaskManagerTests()
        {
            _agentRegistryMock = new Mock<IAgentRegistry>();
            _executorMock = new Mock<IAgentExecutor>();
            _loggerMock = new Mock<ILogger<BackgroundTaskManager>>();
            _manager = new BackgroundTaskManager(_agentRegistryMock.Object, _executorMock.Object, _loggerMock.Object);
        }

        /// <summary>
        /// 创建模拟的 Agent 执行序列
        /// </summary>
        private static async IAsyncEnumerable<IMessageEvent> CreateAgentResponse(
            string content,
            int delayMs = 100,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Delay(delayMs, ct);
            yield return new StreamCompleteEvent
            {
                SessionId = "",
                Message = new ChatMessage { Role = ChatRole.Assistant, Content = content }
            };
        }

        /// <summary>
        /// 创建长时间运行的 Agent 执行序列（可取消）
        /// </summary>
        private static async IAsyncEnumerable<IMessageEvent> CreateLongRunningAgentResponse(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Delay(10000, ct);
            yield return new StreamCompleteEvent
            {
                SessionId = "",
                Message = new ChatMessage { Role = ChatRole.Assistant, Content = "Test output" }
            };
        }

        /// <summary>
        /// 创建模拟的 Agent 定义 + 执行器 Setup
        /// </summary>
        private void SetupAgent(string content, int delayMs = 100)
        {
            var definition = new AgentDefinition { Name = "test-agent" };
            _executorMock.Setup(r => r.ExecuteAsync(
                    It.IsAny<AgentDefinition>(),
                    It.IsAny<IReadOnlyList<ChatMessage>>(),
                    It.IsAny<AgentContext>(),
                    It.IsAny<CancellationToken>()))
                .Returns((AgentDefinition _, IReadOnlyList<ChatMessage> _, AgentContext _, CancellationToken ct) =>
                    CreateAgentResponse(content, delayMs, ct));

            _agentRegistryMock.Setup(r => r.GetAgentAsync("test-agent"))
                .ReturnsAsync(definition);
        }

        /// <summary>
        /// 创建长时间运行的 Agent 定义 + 执行器 Setup
        /// </summary>
        private void SetupLongRunningAgent()
        {
            var definition = new AgentDefinition { Name = "test-agent" };
            _executorMock.Setup(r => r.ExecuteAsync(
                    It.IsAny<AgentDefinition>(),
                    It.IsAny<IReadOnlyList<ChatMessage>>(),
                    It.IsAny<AgentContext>(),
                    It.IsAny<CancellationToken>()))
                .Returns((AgentDefinition _, IReadOnlyList<ChatMessage> _, AgentContext _, CancellationToken ct) =>
                    CreateLongRunningAgentResponse(ct));

            _agentRegistryMock.Setup(r => r.GetAgentAsync("test-agent"))
                .ReturnsAsync(definition);
        }

        [Fact]
        public async Task StartAsync_ShouldCreateTaskWithPendingStatus()
        {
            // Arrange
            SetupAgent("Test output", 100);

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
            taskId.Should().StartWith("tmp_");

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
            SetupLongRunningAgent();

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
            SetupAgent("Test output", 100);

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
            SetupAgent("Test output", 100);

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
            SetupAgent("Test output", 0);

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
            SetupLongRunningAgent();

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
        public async Task CancelBySessionAsync_ShouldCancelRelatedTasks()
        {
            SetupLongRunningAgent();

            var parentId = "parent-session";
            await _manager.StartAsync(new BackgroundTaskLaunchArgs
            {
                TaskId = "child-1",
                AgentName = "test-agent",
                Input = new ChatMessage { Role = ChatRole.User, Content = "A" },
                Context = new AgentContext
                {
                    SessionId = "child-1",
                    ParentSessionId = parentId,
                    MessageId = "m1"
                }
            });

            await _manager.StartAsync(new BackgroundTaskLaunchArgs
            {
                TaskId = "other-1",
                AgentName = "test-agent",
                Input = new ChatMessage { Role = ChatRole.User, Content = "B" },
                Context = new AgentContext
                {
                    SessionId = "other-1",
                    ParentSessionId = "other-parent",
                    MessageId = "m2"
                }
            });

            await Task.Delay(100);

            var count = await _manager.CancelBySessionAsync(parentId);
            count.Should().Be(1);

            var child = await _manager.GetAsync("child-1");
            child!.Status.Should().Be(BackgroundTaskStatus.Cancelled);

            var other = await _manager.GetAsync("other-1");
            other!.Status.Should().Be(BackgroundTaskStatus.Running);
        }

        [Fact]
        public async Task GetOutputAsync_ShouldReturnResult_WhenTaskCompleted()
        {
            // Arrange
            SetupAgent("Hello from agent", 0);

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
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Core.Todo;
using Xunit;

namespace Seeing.Agent.Tests.Todo
{
    /// <summary>
    /// TodoManager 单元测试
    /// </summary>
    public class TodoManagerTests : IDisposable
    {
        private readonly Mock<ILogger<TodoManager>> _loggerMock;
        private readonly string _testDirectory;

        public TodoManagerTests()
        {
            _loggerMock = new Mock<ILogger<TodoManager>>();
            _testDirectory = Path.Combine(Path.GetTempPath(), $"todo_test_{Guid.NewGuid():N}");
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
        public async Task LoadAsync_WhenFileNotExists_ReturnsEmptyTodoList()
        {
            // Arrange
            var manager = new TodoManager(_loggerMock.Object, _testDirectory);
            var sessionId = "test_session_001";

            // Act
            var result = await manager.LoadAsync(sessionId);

            // Assert
            result.Should().NotBeNull();
            result.SessionId.Should().Be(sessionId);
            result.Items.Should().BeEmpty();
        }

        [Fact]
        public async Task SaveAsync_CreatesFileAndSavesTodoList()
        {
            // Arrange
            var manager = new TodoManager(_loggerMock.Object, _testDirectory);
            var sessionId = "test_session_002";
            var todoList = new TodoList
            {
                SessionId = sessionId,
                Items = new List<TodoItem>
                {
                    new TodoItem { Id = "todo_001", Content = "测试任务", Priority = "high" }
                }
            };

            // Act
            await manager.SaveAsync(todoList);

            // Assert
            var filePath = Path.Combine(_testDirectory, $"{sessionId}.json");
            File.Exists(filePath).Should().BeTrue();
        }

        [Fact]
        public async Task AddAsync_CreatesNewTodoItem()
        {
            // Arrange
            var manager = new TodoManager(_loggerMock.Object, _testDirectory);
            var sessionId = "test_session_003";
            var content = "完成代码审查";
            var priority = "high";

            // Act
            var result = await manager.AddAsync(sessionId, content, priority);

            // Assert
            result.Should().NotBeNull();
            result.Id.Should().StartWith("todo_");
            result.Content.Should().Be(content);
            result.Status.Should().Be(TodoStatus.Pending);
            result.Priority.Should().Be(priority);
            result.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
            result.CompletedAt.Should().BeNull();

            // 验证持久化
            var loaded = await manager.LoadAsync(sessionId);
            loaded.Items.Should().HaveCount(1);
            loaded.Items[0].Content.Should().Be(content);
        }

        [Fact]
        public async Task UpdateStatusAsync_UpdatesTodoItemStatus()
        {
            // Arrange
            var manager = new TodoManager(_loggerMock.Object, _testDirectory);
            var sessionId = "test_session_004";
            var addedItem = await manager.AddAsync(sessionId, "待完成任务", "medium");

            // Act - 标记为进行中
            var result = await manager.UpdateStatusAsync(sessionId, addedItem.Id, TodoStatus.InProgress);

            // Assert
            result.Should().NotBeNull();
            result!.Status.Should().Be(TodoStatus.InProgress);
            result.CompletedAt.Should().BeNull();

            // Act - 标记为已完成
            var completedResult = await manager.UpdateStatusAsync(sessionId, addedItem.Id, TodoStatus.Completed);

            // Assert
            completedResult!.Status.Should().Be(TodoStatus.Completed);
            completedResult.CompletedAt.Should().NotBeNull();
            completedResult.CompletedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
        }

        [Fact]
        public async Task LoadAsync_AfterSave_ReturnsSavedTodoList()
        {
            // Arrange
            var manager = new TodoManager(_loggerMock.Object, _testDirectory);
            var sessionId = "test_session_005";
            
            await manager.AddAsync(sessionId, "任务一", "high");
            await manager.AddAsync(sessionId, "任务二", "medium");
            await manager.AddAsync(sessionId, "任务三", "low");

            // Act
            var result = await manager.LoadAsync(sessionId);

            // Assert
            result.Should().NotBeNull();
            result.SessionId.Should().Be(sessionId);
            result.Items.Should().HaveCount(3);
            result.Items[0].Content.Should().Be("任务一");
            result.Items[0].Priority.Should().Be("high");
            result.Items[1].Content.Should().Be("任务二");
            result.Items[1].Priority.Should().Be("medium");
            result.Items[2].Content.Should().Be("任务三");
            result.Items[2].Priority.Should().Be("low");
        }

        [Fact]
        public async Task DeleteAsync_RemovesTodoFile()
        {
            // Arrange
            var manager = new TodoManager(_loggerMock.Object, _testDirectory);
            var sessionId = "test_session_006";
            await manager.AddAsync(sessionId, "待删除任务", "low");

            // 验证文件已创建
            var filePath = Path.Combine(_testDirectory, $"{sessionId}.json");
            File.Exists(filePath).Should().BeTrue();

            // Act
            await manager.DeleteAsync(sessionId);

            // Assert
            File.Exists(filePath).Should().BeFalse();
            
            // 验证重新加载返回空列表
            var result = await manager.LoadAsync(sessionId);
            result.Items.Should().BeEmpty();
        }
    }
}
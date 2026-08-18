using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Todo;
using Seeing.Agent.Todo;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Todo;

/// <summary>
/// SessionContextTodoStore 单元测试（ITodoStore 端口-适配器）
/// </summary>
public class SessionContextTodoStoreTests
{
    /// <summary>
    /// 构建被测对象：store + 会话管理器 Mock + 会话实例
    /// </summary>
    private static (SessionContextTodoStore store, Mock<ISessionManager> manager, SessionData session) CreateSut()
    {
        var session = new SessionData { Id = "s1" };
        var manager = new Mock<ISessionManager>();
        manager.Setup(m => m.Get("s1")).Returns(session);
        return (new SessionContextTodoStore(manager.Object), manager, session);
    }

    [Fact]
    public async Task SaveAsync_Then_LoadAsync_ShouldRoundtrip()
    {
        // Arrange
        var (store, _, _) = CreateSut();
        var todos = new TodoList
        {
            SessionId = "s1",
            Items = { new TodoItem { Content = "任务1", Status = TodoStatus.Pending } }
        };

        // Act
        await store.SaveAsync("s1", todos);
        var loaded = await store.LoadAsync("s1");

        // Assert
        loaded.Items.Should().HaveCount(1);
        loaded.Items[0].Content.Should().Be("任务1");
        loaded.Items[0].Status.Should().Be(TodoStatus.Pending);
    }

    [Fact]
    public async Task LoadAsync_WithoutSave_ShouldReturnEmptyList()
    {
        // Arrange
        var (store, _, _) = CreateSut();

        // Act
        var loaded = await store.LoadAsync("s1");

        // Assert
        loaded.Items.Should().BeEmpty();
        loaded.SessionId.Should().Be("s1");
    }

    [Fact]
    public async Task SaveAsync_WhenSessionMissing_ShouldThrow()
    {
        // Arrange
        var (store, manager, _) = CreateSut();
        manager.Setup(m => m.Get("missing")).Returns((SessionData?)null);
        var todos = new TodoList { SessionId = "missing" };

        // Act
        var act = async () => await store.SaveAsync("missing", todos);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*会话不存在*");
    }
}
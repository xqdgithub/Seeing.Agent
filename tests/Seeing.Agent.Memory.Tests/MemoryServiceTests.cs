using FluentAssertions;
using Moq;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core;
using Xunit;

namespace Seeing.Agent.Memory.Tests;

/// <summary>
/// Task 28: MemoryService 单元测试
/// 测试 MemoryManager 的基本功能
/// </summary>
public class MemoryServiceTests
{
    private readonly Mock<IMemoryRepository> _repositoryMock;
    private readonly Mock<IMemoryRetriever> _retrieverMock;
    private readonly MemoryManager _memoryManager;

    public MemoryServiceTests()
    {
        _repositoryMock = new Mock<IMemoryRepository>();
        _retrieverMock = new Mock<IMemoryRetriever>();
        _memoryManager = new MemoryManager(_repositoryMock.Object, _retrieverMock.Object);
    }

    [Fact(DisplayName = "初始化应成功")]
    public async Task Initialize_ShouldSucceed()
    {
        // Act
        await _memoryManager.InitializeAsync();

        // Assert - 无异常即为成功
        true.Should().BeTrue();
    }

    [Fact(DisplayName = "创建记忆应调用 Repository")]
    public async Task CreateMemory_ShouldCallRepository()
    {
        // Arrange
        var memory = CreateTestMemory("test-001");
        _repositoryMock.Setup(r => r.SaveMemoryAsync(memory))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _memoryManager.CreateMemoryAsync(memory);

        // Assert
        result.Should().Be("test-001");
        _repositoryMock.Verify(r => r.SaveMemoryAsync(memory), Times.Once);
    }

    [Fact(DisplayName = "空记忆应抛出异常")]
    public async Task CreateMemory_WithNull_ShouldThrowException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => _memoryManager.CreateMemoryAsync(null!));
    }

    [Fact(DisplayName = "空ID应抛出异常")]
    public async Task CreateMemory_WithEmptyId_ShouldThrowException()
    {
        // Arrange
        var metadata = new MemoryMetadata("session", "agent", "source", new[] { "tag" }, 0.5, 0.5);
        var memory = new MemoryEntry("", MemoryType.Semantic, "content", metadata, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _memoryManager.CreateMemoryAsync(memory));
    }

    [Fact(DisplayName = "获取记忆应返回正确结果")]
    public async Task GetMemory_ShouldReturnCorrectEntry()
    {
        // Arrange
        var memory = CreateTestMemory("test-002");
        _repositoryMock.Setup(r => r.GetMemoryAsync("test-002"))
            .ReturnsAsync(memory);

        // Act
        var result = await _memoryManager.GetMemoryAsync("test-002");

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be("test-002");
    }

    [Fact(DisplayName = "不存在的记忆应返回 null")]
    public async Task GetMemory_NotExist_ShouldReturnNull()
    {
        // Arrange
        _repositoryMock.Setup(r => r.GetMemoryAsync("not-exist"))
            .ReturnsAsync(null!);

        // Act
        var result = await _memoryManager.GetMemoryAsync("not-exist");

        // Assert
        result.Should().BeNull();
    }

    [Fact(DisplayName = "更新记忆应调用 Repository")]
    public async Task UpdateMemory_ShouldCallRepository()
    {
        // Arrange
        var existingMemory = CreateTestMemory("test-003", "原始内容");
        var update = new MemoryUpdate { Content = "更新内容" };

        _repositoryMock.Setup(r => r.GetMemoryAsync("test-003"))
            .ReturnsAsync(existingMemory);
        _repositoryMock.Setup(r => r.SaveMemoryAsync(It.IsAny<MemoryEntry>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _memoryManager.UpdateMemoryAsync("test-003", update);

        // Assert
        result.Content.Should().Be("更新内容");
        _repositoryMock.Verify(r => r.SaveMemoryAsync(It.IsAny<MemoryEntry>()), Times.Once);
    }

    [Fact(DisplayName = "删除记忆应调用 Repository")]
    public async Task DeleteMemory_ShouldCallRepository()
    {
        // Arrange
        _repositoryMock.Setup(r => r.DeleteMemoryAsync("test-004"))
            .Returns(Task.CompletedTask);

        // Act
        await _memoryManager.DeleteMemoryAsync("test-004");

        // Assert
        _repositoryMock.Verify(r => r.DeleteMemoryAsync("test-004"), Times.Once);
    }

    [Fact(DisplayName = "搜索记忆应调用 Retriever")]
    public async Task SearchMemories_ShouldCallRetriever()
    {
        // Arrange
        var memories = new List<MemoryEntry> { CreateTestMemory("test-005") };
        _retrieverMock.Setup(r => r.RetrieveAsync("query"))
            .ReturnsAsync(memories);

        // Act
        var result = await _memoryManager.SearchMemoriesAsync("query");

        // Assert
        result.Should().NotBeNull();
        result.TotalCount.Should().Be(1);
        _retrieverMock.Verify(r => r.RetrieveAsync("query"), Times.Once);
    }

    /// <summary>
    /// 创建测试记忆条目
    /// </summary>
    private static MemoryEntry CreateTestMemory(string id, string content = "测试内容")
    {
        var metadata = new MemoryMetadata(
            SessionId: "test-session",
            AgentId: "test-agent",
            Source: "test-source",
            Tags: new[] { "test" },
            Confidence: 0.8,
            Importance: 0.9
        );

        return new MemoryEntry(
            Id: id,
            Type: MemoryType.Semantic,
            Content: content,
            Metadata: metadata,
            CreatedAt: DateTimeOffset.UtcNow,
            ValidAt: DateTimeOffset.UtcNow,
            InvalidAt: null
        );
    }
}
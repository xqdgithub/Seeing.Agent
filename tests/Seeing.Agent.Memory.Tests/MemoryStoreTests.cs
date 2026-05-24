using FluentAssertions;
using Seeing.Agent.Memory.Core;
using Xunit;

namespace Seeing.Agent.Memory.Tests;

/// <summary>
/// Task 27: MemoryStore 单元测试
/// 测试 MdMemoryRepository 的基本功能
/// </summary>
public class MemoryStoreTests : IDisposable
{
    private readonly string _testDirectory;

    public MemoryStoreTests()
    {
        // 使用临时目录作为测试存储
        _testDirectory = Path.Combine(Path.GetTempPath(), $"memory_test_{Guid.NewGuid():N}");
    }

    [Fact(DisplayName = "创建 Repository 实例应成功")]
    public void CreateRepository_ShouldSucceed()
    {
        // Arrange & Act
        var repository = new MdMemoryRepository(_testDirectory);

        // Assert
        repository.Should().NotBeNull();
        Directory.Exists(_testDirectory).Should().BeTrue();
    }

    [Fact(DisplayName = "保存记忆应创建对应的文件")]
    public async Task SaveMemory_ShouldCreateFile()
    {
        // Arrange
        var repository = new MdMemoryRepository(_testDirectory);
        var memory = CreateTestMemory("test-id-001");

        // Act
        await repository.SaveMemoryAsync(memory);

        // Assert
        var semanticDir = Path.Combine(_testDirectory, "semantic");
        Directory.Exists(semanticDir).Should().BeTrue();
        var files = Directory.GetFiles(semanticDir, "*test-id-001.md");
        files.Should().HaveCount(1);
    }

    [Fact(DisplayName = "读取记忆应返回正确内容")]
    public async Task GetMemory_ShouldReturnCorrectEntry()
    {
        // Arrange
        var repository = new MdMemoryRepository(_testDirectory);
        var memory = CreateTestMemory("test-id-002", "测试内容");
        await repository.SaveMemoryAsync(memory);

        // Act
        var result = await repository.GetMemoryAsync("test-id-002");

        // Assert
        result.Should().NotBeNull();
        var entry = result as MemoryEntry;
        entry.Should().NotBeNull();
        entry!.Id.Should().Be("test-id-002");
        entry.Content.Should().Contain("测试内容");
    }

    [Fact(DisplayName = "删除记忆应移除文件")]
    public async Task DeleteMemory_ShouldRemoveFile()
    {
        // Arrange
        var repository = new MdMemoryRepository(_testDirectory);
        var memory = CreateTestMemory("test-id-003");
        await repository.SaveMemoryAsync(memory);

        // Act
        await repository.DeleteMemoryAsync("test-id-003");

        // Assert
        var result = await repository.GetMemoryAsync("test-id-003");
        result.Should().BeNull();
    }

    [Fact(DisplayName = "列出记忆应返回所有条目")]
    public async Task ListMemories_ShouldReturnAllEntries()
    {
        // Arrange
        var repository = new MdMemoryRepository(_testDirectory);
        await repository.SaveMemoryAsync(CreateTestMemory("test-id-004"));
        await repository.SaveMemoryAsync(CreateTestMemory("test-id-005"));

        // Act
        var memories = await repository.ListMemoriesAsync();

        // Assert
        memories.Should().HaveCount(2);
    }

    [Fact(DisplayName = "无效ID应抛出异常")]
    public async Task InvalidId_ShouldThrowException()
    {
        // Arrange
        var repository = new MdMemoryRepository(_testDirectory);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => repository.GetMemoryAsync(""));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.GetMemoryAsync("test/../invalid"));
    }

    /// <summary>
    /// 创建测试记忆条目
    /// </summary>
    private MemoryEntry CreateTestMemory(string id, string content = "默认内容")
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

    /// <summary>
    /// 清理测试目录
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
            catch
            {
                // 忽略清理失败
            }
        }
    }
}
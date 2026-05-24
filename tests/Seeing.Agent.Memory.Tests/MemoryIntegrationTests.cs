using FluentAssertions;
using Seeing.Agent.Memory.Core;
using Xunit;

namespace Seeing.Agent.Memory.Tests;

/// <summary>
/// Task 31: 集成测试
/// 测试 Memory 系统的端到端流程
/// </summary>
public class MemoryIntegrationTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly MdMemoryRepository _repository;
    private readonly MemoryRetriever _retriever;
    private readonly MemoryManager _memoryManager;

    public MemoryIntegrationTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"memory_integration_{Guid.NewGuid():N}");
        _repository = new MdMemoryRepository(_testDirectory);
        _retriever = new MemoryRetriever(_repository);
        _memoryManager = new MemoryManager(_repository, _retriever);
    }

    [Fact(DisplayName = "完整 CRUD 流程应成功")]
    public async Task FullCrudFlow_ShouldSucceed()
    {
        // Arrange
        var memory = CreateTestMemory("integration-001", "集成测试内容");

        // Act - Create
        await _memoryManager.InitializeAsync();
        var id = await _memoryManager.CreateMemoryAsync(memory);

        // Assert - Create
        id.Should().Be("integration-001");

        // Act - Read
        var retrieved = await _memoryManager.GetMemoryAsync("integration-001");

        // Assert - Read
        retrieved.Should().NotBeNull();
        retrieved!.Content.Trim().Should().Be("集成测试内容");

        // Act - Update
        var update = new MemoryUpdate { Content = "更新后的内容" };
        var updated = await _memoryManager.UpdateMemoryAsync("integration-001", update);

        // Assert - Update
        updated.Content.Should().Be("更新后的内容");

        // Act - Delete
        await _memoryManager.DeleteMemoryAsync("integration-001");

        // Assert - Delete
        var deleted = await _memoryManager.GetMemoryAsync("integration-001");
        deleted.Should().BeNull();
    }

    [Fact(DisplayName = "搜索流程应返回正确结果")]
    public async Task SearchFlow_ShouldReturnCorrectResults()
    {
        // Arrange
        await _memoryManager.InitializeAsync();
        await _memoryManager.CreateMemoryAsync(CreateTestMemory("search-001", "搜索关键词测试"));
        await _memoryManager.CreateMemoryAsync(CreateTestMemory("search-002", "其他内容"));

        // Act
        var result = await _memoryManager.SearchMemoriesAsync("搜索");

        // Assert
        result.Should().NotBeNull();
        result.TotalCount.Should().BeGreaterOrEqualTo(1);
    }

    [Fact(DisplayName = "列表查询应返回所有记忆")]
    public async Task ListFlow_ShouldReturnAllMemories()
    {
        // Arrange
        await _memoryManager.InitializeAsync();
        await _memoryManager.CreateMemoryAsync(CreateTestMemory("list-001"));
        await _memoryManager.CreateMemoryAsync(CreateTestMemory("list-002"));
        await _memoryManager.CreateMemoryAsync(CreateTestMemory("list-003"));

        // Act
        var memories = await _memoryManager.ListMemoriesAsync();

        // Assert
        memories.Should().HaveCount(3);
    }

    [Fact(DisplayName = "多类型记忆应正确分类存储")]
    public async Task MultipleTypes_ShouldBeStoredCorrectly()
    {
        // Arrange
        await _memoryManager.InitializeAsync();

        var semanticMemory = CreateTestMemory("semantic-001", type: MemoryType.Semantic);
        var episodicMemory = CreateTestMemory("episodic-001", type: MemoryType.Episodic);
        var proceduralMemory = CreateTestMemory("procedural-001", type: MemoryType.Procedural);

        // Act
        await _memoryManager.CreateMemoryAsync(semanticMemory);
        await _memoryManager.CreateMemoryAsync(episodicMemory);
        await _memoryManager.CreateMemoryAsync(proceduralMemory);

        // Assert
        Directory.Exists(Path.Combine(_testDirectory, "semantic")).Should().BeTrue();
        Directory.Exists(Path.Combine(_testDirectory, "episodic")).Should().BeTrue();
        Directory.Exists(Path.Combine(_testDirectory, "procedural")).Should().BeTrue();
    }

    /// <summary>
    /// 创建测试记忆条目
    /// </summary>
    private static MemoryEntry CreateTestMemory(
        string id,
        string content = "默认内容",
        MemoryType type = MemoryType.Semantic)
    {
        var metadata = new MemoryMetadata(
            SessionId: "integration-session",
            AgentId: "integration-agent",
            Source: "integration-test",
            Tags: new[] { "integration" },
            Confidence: 0.8,
            Importance: 0.9
        );

        return new MemoryEntry(
            Id: id,
            Type: type,
            Content: content,
            Metadata: metadata,
            CreatedAt: DateTimeOffset.UtcNow,
            ValidAt: DateTimeOffset.UtcNow,
            InvalidAt: null
        );
    }

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
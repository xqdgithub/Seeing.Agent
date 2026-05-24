using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core;
using Xunit;

namespace Seeing.Agent.Memory.Tests;

/// <summary>
/// Task 30: MemoryForgetting 单元测试
/// 测试 MemoryForgetManager 的基本功能
/// </summary>
public class MemoryForgettingTests
{
    private readonly Mock<IMemoryRepository> _repositoryMock;
    private readonly Mock<IMemoryScorer> _scorerMock;
    private readonly MemoryOptions _options;
    private readonly MemoryForgetManager _forgetManager;

    public MemoryForgettingTests()
    {
        _repositoryMock = new Mock<IMemoryRepository>();
        _scorerMock = new Mock<IMemoryScorer>();
        _options = new MemoryOptions();

        var optionsWrapper = Options.Create(_options);
        _forgetManager = new MemoryForgetManager(
            _repositoryMock.Object,
            _scorerMock.Object,
            optionsWrapper);
    }

    [Fact(DisplayName = "获取遗忘候选应返回正确列表")]
    public async Task GetForgettingCandidates_ShouldReturnCorrectList()
    {
        // Arrange
        var lowScoreMemory = CreateTestMemory("low-001", importance: 0.1);
        var highScoreMemory = CreateTestMemory("high-001", importance: 0.9);

        _repositoryMock.Setup(r => r.ListMemoriesAsync())
            .ReturnsAsync(new[] { lowScoreMemory, highScoreMemory });
        _scorerMock.Setup(s => s.ScoreAsync(lowScoreMemory, It.IsAny<IDictionary<string, object>>()))
            .ReturnsAsync(0.2); // 低于默认阈值 0.5
        _scorerMock.Setup(s => s.ScoreAsync(highScoreMemory, It.IsAny<IDictionary<string, object>>()))
            .ReturnsAsync(0.8); // 高于默认阈值 0.5

        // Act
        var candidates = await _forgetManager.GetForgettingCandidatesAsync(0.5);

        // Assert
        candidates.Should().HaveCount(1);
        candidates[0].Id.Should().Be("low-001");
    }

    [Fact(DisplayName = "自定义阈值应生效")]
    public async Task GetForgettingCandidates_WithCustomThreshold_ShouldWork()
    {
        // Arrange
        var memory = CreateTestMemory("test-001", importance: 0.3);

        _repositoryMock.Setup(r => r.ListMemoriesAsync())
            .ReturnsAsync(new[] { memory });
        _scorerMock.Setup(s => s.ScoreAsync(memory, It.IsAny<IDictionary<string, object>>()))
            .ReturnsAsync(0.35);

        // Act - 阈值 0.4，记忆分数 0.35 应被列入候选
        var candidates = await _forgetManager.GetForgettingCandidatesAsync(0.4);

        // Assert
        candidates.Should().HaveCount(1);
    }

    [Fact(DisplayName = "归档记忆应创建 Archive 类型")]
    public async Task ArchiveLowScoreMemories_ShouldCreateArchiveType()
    {
        // Arrange
        var lowScoreMemory = CreateTestMemory("low-002", importance: 0.1);

        _repositoryMock.Setup(r => r.ListMemoriesAsync())
            .ReturnsAsync(new[] { lowScoreMemory });
        _scorerMock.Setup(s => s.ScoreAsync(lowScoreMemory, It.IsAny<IDictionary<string, object>>()))
            .ReturnsAsync(0.2);
        _repositoryMock.Setup(r => r.SaveMemoryAsync(It.IsAny<MemoryEntry>()))
            .Returns(Task.CompletedTask);

        // Act
        var count = await _forgetManager.ArchiveLowScoreMemoriesAsync(0.5);

        // Assert
        count.Should().Be(1);
        _repositoryMock.Verify(r => r.SaveMemoryAsync(It.Is<MemoryEntry>(e => e.Type == MemoryType.Archive)), Times.Once);
    }

    [Fact(DisplayName = "被动衰减应降低重要性")]
    public async Task ApplyDecay_ShouldReduceImportance()
    {
        // Arrange - 创建超过 7 天的记忆
        var oldMemory = CreateTestMemory(
            "old-001",
            importance: 0.8,
            validAt: DateTimeOffset.UtcNow.AddDays(-30));

        _repositoryMock.Setup(r => r.ListMemoriesAsync())
            .ReturnsAsync(new[] { oldMemory });
        _repositoryMock.Setup(r => r.SaveMemoryAsync(It.IsAny<MemoryEntry>()))
            .Returns(Task.CompletedTask);

        // Act
        var count = await _forgetManager.ApplyDecayAsync();

        // Assert
        count.Should().BeGreaterOrEqualTo(1);
        _repositoryMock.Verify(r => r.SaveMemoryAsync(It.IsAny<MemoryEntry>()), Times.Once);
    }

    [Fact(DisplayName = "新记忆不应被衰减")]
    public async Task ApplyDecay_NewMemory_ShouldNotDecay()
    {
        // Arrange - 创建新的记忆（不到 7 天）
        var newMemory = CreateTestMemory(
            "new-001",
            importance: 0.8,
            validAt: DateTimeOffset.UtcNow.AddDays(-1));

        _repositoryMock.Setup(r => r.ListMemoriesAsync())
            .ReturnsAsync(new[] { newMemory });

        // Act
        var count = await _forgetManager.ApplyDecayAsync();

        // Assert
        count.Should().Be(0);
        _repositoryMock.Verify(r => r.SaveMemoryAsync(It.IsAny<MemoryEntry>()), Times.Never);
    }

    [Fact(DisplayName = "取消令牌应中断操作")]
    public async Task GetForgettingCandidates_WithCancellation_ShouldThrow()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();

        _repositoryMock.Setup(r => r.ListMemoriesAsync())
            .ReturnsAsync(new[] { CreateTestMemory("test-001") });

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _forgetManager.GetForgettingCandidatesAsync(0.5, cts.Token));
    }

    /// <summary>
    /// 创建测试记忆条目
    /// </summary>
    private static MemoryEntry CreateTestMemory(
        string id,
        double importance = 0.5,
        DateTimeOffset? validAt = null)
    {
        var metadata = new MemoryMetadata(
            SessionId: "test-session",
            AgentId: "test-agent",
            Source: "test-source",
            Tags: new[] { "test" },
            Confidence: 0.8,
            Importance: importance
        );

        return new MemoryEntry(
            Id: id,
            Type: MemoryType.Semantic,
            Content: "测试内容",
            Metadata: metadata,
            CreatedAt: DateTimeOffset.UtcNow,
            ValidAt: validAt ?? DateTimeOffset.UtcNow,
            InvalidAt: null
        );
    }
}
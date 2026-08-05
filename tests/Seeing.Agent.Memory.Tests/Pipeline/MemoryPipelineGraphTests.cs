using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Graph;
using Seeing.Agent.Memory.Core.Models;
using Seeing.Agent.Memory.Core.Pipeline;
using Seeing.Agent.Memory.Core.Storage;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Pipeline;

public class MemoryPipelineGraphTests : IDisposable
{
    private readonly string _dir;

    public MemoryPipelineGraphTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "seeing-memory-pipeline-graph-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task ProcessAsync_AcceptedExtraction_ShouldUpdateGraphWithNodeAndEdges()
    {
        // Arrange
        var filter = new Mock<IMemoryHeuristicFilter>();
        filter.Setup(f => f.Evaluate(It.IsAny<MemoryCandidate>()))
            .Returns(new FilterDecision(true, null));

        var extractor = new Mock<IMemoryExtractor>();
        extractor.Setup(e => e.ExtractBatchAsync(It.IsAny<IReadOnlyList<MemoryCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ExtractionResult("Title", "Content about [[other]]. More text here for length.", 0.9,
                    new[] { "t1" }, "fact")
            });
        extractor.Setup(e => e.ExtractAsync(It.IsAny<MemoryCandidate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExtractionResult("Title", "Content about [[other]]. More text here for length.", 0.9,
                new[] { "t1" }, "fact"));

        var index = new Mock<IMemoryIndex>();
        index.Setup(i => i.IndexAsync(It.IsAny<FileNode>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var graph = new Mock<IMemoryGraph>();
        var store = new LocalFileStore(_dir, NullLogger<LocalFileStore>.Instance);
        var pipeline = new MemoryPipeline(
            filter.Object,
            extractor.Object,
            store,
            index.Object,
            graph.Object,
            Options.Create(new MemoryOptions()),
            NullLogger<MemoryPipeline>.Instance);

        var candidate = new MemoryCandidate(
            "abc12345",
            "sess1",
            null,
            MemorySource.Chat,
            null,
            "用户偏好 [[PostgreSQL]]，要求 API 分页。",
            DateTimeOffset.UtcNow);

        // Act
        var result = await pipeline.ProcessAsync(candidate, CancellationToken.None);

        // Assert
        result.Stored.Should().BeTrue();
        // 验证图谱更新被调用
        graph.Verify(g => g.AddNodeAsync(
            It.Is<string>(p => p.StartsWith("daily/")),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // 验证 Wikilink 边被创建（mock extractor 返回 "Content about [[other]]..."）
        graph.Verify(g => g.AddEdgeAsync(
            It.Is<string>(p => p.StartsWith("daily/")),
            It.Is<string>(t => t.EndsWith("other.md")),
            EdgeType.Reference,
            It.IsAny<double>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // 验证标签边被创建
        graph.Verify(g => g.AddEdgeAsync(
            It.Is<string>(p => p.StartsWith("daily/")),
            "tag/t1",
            EdgeType.Tag,
            It.IsAny<double>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // 验证目录父子边
        graph.Verify(g => g.AddEdgeAsync(
            It.Is<string>(p => p.StartsWith("daily/") && p.Contains("/202")),
            It.Is<string>(p => p.StartsWith("daily/")),
            EdgeType.ParentChild,
            It.IsAny<double>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_NoWikilinks_ShouldStillAddNode()
    {
        // Arrange
        var filter = new Mock<IMemoryHeuristicFilter>();
        filter.Setup(f => f.Evaluate(It.IsAny<MemoryCandidate>()))
            .Returns(new FilterDecision(true, null));

        var extractor = new Mock<IMemoryExtractor>();
        extractor.Setup(e => e.ExtractBatchAsync(It.IsAny<IReadOnlyList<MemoryCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ExtractionResult("Simple Note", "Just content no links.", 0.6,
                    new[] { "simple" }, "note")
            });
        extractor.Setup(e => e.ExtractAsync(It.IsAny<MemoryCandidate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExtractionResult("Simple Note", "Just content no links.", 0.6,
                new[] { "simple" }, "note"));

        var index = new Mock<IMemoryIndex>();
        var graph = new Mock<IMemoryGraph>();
        var store = new LocalFileStore(_dir, NullLogger<LocalFileStore>.Instance);
        var pipeline = new MemoryPipeline(
            filter.Object,
            extractor.Object,
            store,
            index.Object,
            graph.Object,
            Options.Create(new MemoryOptions()),
            NullLogger<MemoryPipeline>.Instance);

        var candidate = new MemoryCandidate(
            "xyz789",
            "sess2",
            null,
            MemorySource.Chat,
            null,
            "simple note",
            DateTimeOffset.UtcNow);

        // Act
        await pipeline.ProcessAsync(candidate, CancellationToken.None);

        // Assert
        graph.Verify(g => g.AddNodeAsync(
            It.Is<string>(p => p.StartsWith("daily/")),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // 没有 Wikilink，不应创建 Reference 边
        graph.Verify(g => g.AddEdgeAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            EdgeType.Reference,
            It.IsAny<double>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}

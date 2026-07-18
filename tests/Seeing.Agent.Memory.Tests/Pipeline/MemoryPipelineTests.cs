using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Models;
using Seeing.Agent.Memory.Core.Pipeline;
using Seeing.Agent.Memory.Core.Storage;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Pipeline;

public class MemoryPipelineTests : IDisposable
{
    private readonly string _dir;

    public MemoryPipelineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "seeing-memory-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task ProcessAsync_AcceptedExtraction_ShouldWriteDailyAndSessionIndex()
    {
        var filter = new Mock<IMemoryHeuristicFilter>();
        filter.Setup(f => f.Evaluate(It.IsAny<MemoryCandidate>()))
            .Returns(new FilterDecision(true, null));

        var extractor = new Mock<IMemoryExtractor>();
        extractor.Setup(e => e.ExtractAsync(It.IsAny<MemoryCandidate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExtractionResult("Title", "Content body", 0.9, new[] { "t1" }, "fact"));

        var index = new Mock<IMemoryIndex>();
        index.Setup(i => i.IndexAsync(It.IsAny<FileNode>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var store = new LocalFileStore(_dir, NullLogger<LocalFileStore>.Instance);
        var pipeline = new MemoryPipeline(
            filter.Object,
            extractor.Object,
            store,
            index.Object,
            Options.Create(new MemoryOptions()),
            NullLogger<MemoryPipeline>.Instance);

        var candidate = new MemoryCandidate(
            "abc12345",
            "sess1",
            null,
            MemorySource.Chat,
            null,
            "用户偏好使用 PostgreSQL，并且要求所有 API 必须分页。",
            DateTimeOffset.UtcNow);

        var result = await pipeline.ProcessAsync(candidate, CancellationToken.None);

        result.Stored.Should().BeTrue();
        result.DailyPath.Should().NotBeNullOrEmpty();
        File.Exists(Path.Combine(_dir, result.DailyPath!.Replace('/', Path.DirectorySeparatorChar))).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "session", "sess1", "index.md")).Should().BeTrue();
        index.Verify(i => i.IndexAsync(It.IsAny<FileNode>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}

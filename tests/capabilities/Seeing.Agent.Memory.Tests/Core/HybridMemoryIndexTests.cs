using Microsoft.Data.Sqlite;
using Moq;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core.Graph;
using Seeing.Agent.Memory.Core.Index;
using Seeing.Agent.Memory.Core.Models;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Core;

public class HybridMemoryIndexTests : IDisposable
{
    private readonly SqliteConnection _vectorConnection;
    private readonly SqliteConnection _keywordConnection;
    private readonly Mock<IVectorIndex> _vectorIndexMock;
    private readonly Mock<IKeywordIndex> _keywordIndexMock;
    private readonly Mock<IEmbeddingStatus> _embeddingStatusMock;
    private readonly HybridMemoryIndex _index;

    public HybridMemoryIndexTests()
    {
        _vectorConnection = new SqliteConnection("Data Source=:memory:");
        _keywordConnection = new SqliteConnection("Data Source=:memory:");
        _vectorConnection.Open();
        _keywordConnection.Open();

        _vectorIndexMock = new Mock<IVectorIndex>();
        _keywordIndexMock = new Mock<IKeywordIndex>();
        _embeddingStatusMock = new Mock<IEmbeddingStatus>();
        _embeddingStatusMock.SetupGet(x => x.IsAvailable).Returns(true);

        var fileStore = new Mock<IFileStore>();
        fileStore
            .Setup(f => f.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, CancellationToken _) =>
                FileNode.Create(path, $"# body of {path}", FileMetadata.Create("id", MemoryType.Daily, "title")));

        _index = new HybridMemoryIndex(
            _vectorIndexMock.Object,
            _keywordIndexMock.Object,
            _embeddingStatusMock.Object,
            fileStore.Object);
    }

    [Fact]
    public async Task IndexAsync_ShouldCallBothIndexes()
    {
        // Arrange
        var node = CreateTestNode("test.md");

        // Act
        await _index.IndexAsync(node);

        // Assert
        _vectorIndexMock.Verify(x => x.IndexAsync(node, It.IsAny<CancellationToken>()), Times.Once);
        _keywordIndexMock.Verify(x => x.IndexAsync(node, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IndexAsync_WhenEmbeddingUnavailable_ShouldSkipVector()
    {
        _embeddingStatusMock.SetupGet(x => x.IsAvailable).Returns(false);
        var index = new HybridMemoryIndex(
            _vectorIndexMock.Object,
            _keywordIndexMock.Object,
            _embeddingStatusMock.Object,
            Mock.Of<IFileStore>());
        var node = CreateTestNode("test.md");

        await index.IndexAsync(node);

        _vectorIndexMock.Verify(x => x.IndexAsync(It.IsAny<FileNode>(), It.IsAny<CancellationToken>()), Times.Never);
        _keywordIndexMock.Verify(x => x.IndexAsync(node, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveAsync_ShouldCallBothIndexes()
    {
        // Act
        await _index.RemoveAsync("test.md");

        // Assert
        _vectorIndexMock.Verify(x => x.RemoveAsync("test.md", It.IsAny<CancellationToken>()), Times.Once);
        _keywordIndexMock.Verify(x => x.RemoveAsync("test.md", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RebuildAsync_ShouldClearBothIndexes()
    {
        // Act
        await _index.RebuildAsync();

        // Assert
        _vectorIndexMock.Verify(x => x.ClearAsync(It.IsAny<CancellationToken>()), Times.Once);
        _keywordIndexMock.Verify(x => x.ClearAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SearchAsync_VectorMode_ShouldUseVectorIndex()
    {
        // Arrange
        var query = new SearchQuery("test", SearchMode.Vector);
        _vectorIndexMock.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VectorSearchResult>
            {
                new("test.md", 0.9)
            });

        // Act
        var results = await _index.SearchAsync(query);

        // Assert
        Assert.Single(results);
        Assert.Equal("test.md", results[0].Node.Path);
        Assert.Equal(0.9, results[0].Score);
        _vectorIndexMock.Verify(x => x.SearchAsync("test", 10, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SearchAsync_KeywordMode_ShouldUseKeywordIndex()
    {
        // Arrange
        var query = new SearchQuery("test", SearchMode.Keyword);
        _keywordIndexMock.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<KeywordSearchResult>
            {
                new("test.md", "Test", 1.5)
            });

        // Act
        var results = await _index.SearchAsync(query);

        // Assert
        Assert.Single(results);
        Assert.Equal("test.md", results[0].Node.Path);
        _keywordIndexMock.Verify(x => x.SearchAsync("test", 10, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SearchAsync_HybridMode_ShouldFuseResults()
    {
        // Arrange
        var query = new SearchQuery("test", SearchMode.Hybrid, VectorWeight: 0.6);
        _vectorIndexMock.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VectorSearchResult>
            {
                new("doc1.md", 0.9),
                new("doc2.md", 0.7)
            });
        _keywordIndexMock.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<KeywordSearchResult>
            {
                new("doc2.md", "Doc2", 2.0),
                new("doc3.md", "Doc3", 1.0)
            });

        // Act
        var results = await _index.SearchAsync(query);

        // Assert
        Assert.Equal(3, results.Count); // doc1, doc2, doc3
    }

    [Fact]
    public async Task GetStatsAsync_ShouldReturnStats()
    {
        // Arrange
        _vectorIndexMock.Setup(x => x.GetDocumentCountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(10);
        _keywordIndexMock.Setup(x => x.GetDocumentCountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(15);

        // Act
        var stats = await _index.GetStatsAsync();

        // Assert
        Assert.Equal(10, stats.TotalVectors);
        Assert.Equal(25, stats.TotalDocuments); // 10 + 15
    }

    private static FileNode CreateTestNode(string path)
    {
        return FileNode.Create(path, "Test content", FileMetadata.Create(
            Guid.NewGuid().ToString("N")[..8],
            MemoryType.Session,
            Path.GetFileNameWithoutExtension(path)
        ));
    }

    public void Dispose()
    {
        _vectorConnection?.Dispose();
        _keywordConnection?.Dispose();
    }
}

public class RrfFusionTests
{
    [Fact]
    public void Fuse_EmptyInputs_ShouldReturnEmpty()
    {
        // Act
        var results = RrfFusion.Fuse(
            Array.Empty<(string, double)>(),
            Array.Empty<(string, double)>());

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void Fuse_VectorOnly_ShouldReturnVectorResults()
    {
        // Arrange
        var vectorResults = new List<(string, double)>
        {
            ("doc1.md", 0.9),
            ("doc2.md", 0.7)
        };

        // Act
        var results = RrfFusion.Fuse(
            vectorResults,
            Array.Empty<(string, double)>());

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Equal("doc1.md", results[0].Path);
    }

    [Fact]
    public void Fuse_KeywordOnly_ShouldReturnKeywordResults()
    {
        // Arrange
        var keywordResults = new List<(string, double)>
        {
            ("doc1.md", 2.0),
            ("doc2.md", 1.0)
        };

        // Act
        var results = RrfFusion.Fuse(
            Array.Empty<(string, double)>(),
            keywordResults);

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Equal("doc1.md", results[0].Path);
    }

    [Fact]
    public void Fuse_MixedResults_ShouldFuseCorrectly()
    {
        // Arrange
        var vectorResults = new List<(string, double)>
        {
            ("doc1.md", 0.9),
            ("doc2.md", 0.7)
        };
        var keywordResults = new List<(string, double)>
        {
            ("doc2.md", 2.0),
            ("doc3.md", 1.0)
        };

        // Act
        var results = RrfFusion.Fuse(vectorResults, keywordResults);

        // Assert
        Assert.Equal(3, results.Count); // doc1, doc2, doc3
        // doc2 应该排在前面，因为它在两个列表中都出现
        Assert.Equal("doc2.md", results[0].Path);
    }

    [Fact]
    public void Fuse_WeightedFusion_ShouldRespectWeights()
    {
        // Arrange
        var vectorResults = new List<(string, double)>
        {
            ("doc1.md", 0.9)
        };
        var keywordResults = new List<(string, double)>
        {
            ("doc2.md", 2.0)
        };

        // Act
        var resultsWithHighVectorWeight = RrfFusion.Fuse(
            vectorResults, keywordResults, vectorWeight: 0.8);

        var resultsWithLowVectorWeight = RrfFusion.Fuse(
            vectorResults, keywordResults, vectorWeight: 0.2);

        // Assert
        // 高向量权重时，向量结果应该排在前面
        Assert.Equal("doc1.md", resultsWithHighVectorWeight[0].Path);
        // 低向量权重时，关键词结果应该排在前面
        Assert.Equal("doc2.md", resultsWithLowVectorWeight[0].Path);
    }

    [Fact]
    public void Fuse_InvalidWeight_ShouldThrow()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RrfFusion.Fuse(
                new List<(string, double)> { ("doc.md", 1.0) },
                Array.Empty<(string, double)>(),
                vectorWeight: 1.5));
    }

    [Fact]
    public void Fuse_InvalidK_ShouldThrow()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RrfFusion.Fuse(
                new List<(string, double)> { ("doc.md", 1.0) },
                Array.Empty<(string, double)>(),
                k: 0));
    }
}

using FluentAssertions;
using Microsoft.Data.Sqlite;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core.Index;
using Seeing.Agent.Memory.Core.Models;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Index;

public class VectorIndexTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly VectorIndex _index;
    private readonly MockEmbeddingService _embeddingService;

    public VectorIndexTests()
    {
        // 使用内存数据库进行测试
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _embeddingService = new MockEmbeddingService(dimensions: 128);
        _index = new VectorIndex(_connection, _embeddingService);
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    private static FileNode CreateTestNode(
        string path,
        string content,
        string? title = null,
        string[]? tags = null)
    {
        var metadata = new FileMetadata(
            Id: Guid.NewGuid().ToString("N")[..8],
            Type: MemoryType.Daily,
            Title: title,
            Tags: tags ?? Array.Empty<string>(),
            Importance: 0.5,
            Confidence: 1.0,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: null
        );

        return new FileNode(
            Path: path,
            Content: content,
            ModifiedTime: DateTimeOffset.UtcNow,
            Metadata: metadata,
            Links: Array.Empty<string>()
        );
    }

    [Fact]
    public async Task IndexAsync_StoresVector()
    {
        // Arrange
        var node = CreateTestNode(
            "daily/2025-01-15.md",
            "# Test Document\n\nThis is a test document about machine learning.",
            title: "Test Document"
        );

        // Act
        await _index.IndexAsync(node);
        var count = await _index.GetDocumentCountAsync();

        // Assert
        count.Should().Be(1);
    }

    [Fact]
    public async Task SearchAsync_ReturnsSimilarDocuments()
    {
        // Arrange
        var node1 = CreateTestNode(
            "daily/ml-basics.md",
            "Machine learning is a subset of artificial intelligence. " +
            "It uses algorithms to learn patterns from data.",
            title: "ML Basics"
        );
        var node2 = CreateTestNode(
            "daily/deep-learning.md",
            "Deep learning is a type of machine learning using neural networks. " +
            "It has achieved remarkable success in image and speech recognition.",
            title: "Deep Learning"
        );
        var node3 = CreateTestNode(
            "daily/cooking.md",
            "Cooking is the art of preparing food. " +
            "Recipes include ingredients and step-by-step instructions.",
            title: "Cooking Guide"
        );

        await _index.IndexBatchAsync(new[] { node1, node2, node3 });

        // Act - Search for machine learning related content
        var results = await _index.SearchAsync("machine learning algorithms", limit: 10);

        // Assert
        results.Should().NotBeEmpty();
        // The ML-related documents should rank higher than cooking
        var topPaths = results.Take(2).Select(r => r.Path).ToList();
        topPaths.Should().Contain("daily/ml-basics.md");
        topPaths.Should().Contain("daily/deep-learning.md");
        
        // Cooking should not appear in top results (or have lower score)
        var cookingResult = results.FirstOrDefault(r => r.Path == "daily/cooking.md");
        if (cookingResult != null)
        {
            // If cooking appears, it should have lower score than ML docs
            var mlBasicsResult = results.First(r => r.Path == "daily/ml-basics.md");
            cookingResult.Score.Should().BeLessThan(mlBasicsResult.Score);
        }
    }

    [Fact]
    public async Task SearchAsync_WithFilter()
    {
        // Arrange
        var node1 = CreateTestNode(
            "daily/doc1.md",
            "Machine learning fundamentals and algorithms.",
            title: "ML Fundamentals"
        );
        var node2 = CreateTestNode(
            "session/doc2.md",
            "Machine learning session notes from the meeting.",
            title: "ML Session"
        );
        var node3 = CreateTestNode(
            "daily/doc3.md",
            "Cooking recipes for dinner.",
            title: "Cooking"
        );

        await _index.IndexBatchAsync(new[] { node1, node2, node3 });

        // Act - Filter to only include daily documents
        var results = await _index.SearchWithFilterAsync(
            "machine learning",
            path => path.StartsWith("daily/"),
            limit: 10
        );

        // Assert
        results.Should().NotBeEmpty();
        results.All(r => r.Path.StartsWith("daily/")).Should().BeTrue();
        results.Select(r => r.Path).Should().Contain("daily/doc1.md");
        results.Select(r => r.Path).Should().NotContain("session/doc2.md");
    }

    [Fact]
    public async Task RemoveAsync_DeletesVector()
    {
        // Arrange
        var node = CreateTestNode(
            "daily/to-delete.md",
            "This document will be deleted.",
            title: "To Delete"
        );
        await _index.IndexAsync(node);

        var countBefore = await _index.GetDocumentCountAsync();
        countBefore.Should().Be(1);

        // Act
        await _index.RemoveAsync("daily/to-delete.md");
        var countAfter = await _index.GetDocumentCountAsync();

        // Assert
        countAfter.Should().Be(0);
    }

    [Fact]
    public async Task IndexAsync_UpdatesExistingDocument()
    {
        // Arrange
        var node1 = CreateTestNode(
            "daily/update.md",
            "Original content about cats and dogs.",
            title: "Original Title"
        );
        await _index.IndexAsync(node1);

        // Act - Update the same document
        var node2 = CreateTestNode(
            "daily/update.md",
            "Updated content about machine learning and artificial intelligence.",
            title: "Updated Title"
        );
        await _index.IndexAsync(node2);

        var count = await _index.GetDocumentCountAsync();
        var results = await _index.SearchAsync("machine learning");

        // Assert
        count.Should().Be(1); // Should still have only one document
        results.Should().HaveCount(1);
        results[0].Path.Should().Be("daily/update.md");
    }

    [Fact]
    public async Task IndexBatchAsync_InsertsMultipleDocuments()
    {
        // Arrange
        var nodes = new[]
        {
            CreateTestNode("daily/batch1.md", "Content about neural networks."),
            CreateTestNode("daily/batch2.md", "Content about decision trees."),
            CreateTestNode("daily/batch3.md", "Content about clustering."),
        };

        // Act
        await _index.IndexBatchAsync(nodes);
        var count = await _index.GetDocumentCountAsync();

        // Assert
        count.Should().Be(3);
    }

    [Fact]
    public async Task ClearAsync_RemovesAllDocuments()
    {
        // Arrange
        var nodes = new[]
        {
            CreateTestNode("daily/clear1.md", "Content 1"),
            CreateTestNode("daily/clear2.md", "Content 2"),
        };
        await _index.IndexBatchAsync(nodes);

        var countBefore = await _index.GetDocumentCountAsync();
        countBefore.Should().Be(2);

        // Act
        await _index.ClearAsync();
        var countAfter = await _index.GetDocumentCountAsync();

        // Assert
        countAfter.Should().Be(0);
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_ReturnsEmptyResults()
    {
        // Arrange
        var node = CreateTestNode(
            "daily/test.md",
            "Some content about testing.",
            title: "Test"
        );
        await _index.IndexAsync(node);

        // Act
        var results = await _index.SearchAsync("");

        // Assert
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_ScoreRange()
    {
        // Arrange
        var node = CreateTestNode(
            "daily/score-test.md",
            "Machine learning algorithms for data science.",
            title: "Score Test"
        );
        await _index.IndexAsync(node);

        // Act
        var results = await _index.SearchAsync("machine learning");

        // Assert
        results.Should().NotBeEmpty();
        // Cosine similarity should be between -1 and 1, but for non-negative vectors typically 0-1
        results[0].Score.Should().BeInRange(-1.0, 1.0);
    }
}

/// <summary>
/// Mock Embedding 服务 - 基于文本哈希生成模拟向量
/// 用于测试目的，不提供真实的语义 embedding
/// </summary>
public class MockEmbeddingService : IEmbeddingService
{
    private readonly int _dimensions;
    private readonly Random _random;

    /// <summary>
    /// 创建 Mock Embedding 服务
    /// </summary>
    /// <param name="dimensions">向量维度</param>
    public MockEmbeddingService(int dimensions = 128)
    {
        _dimensions = dimensions;
        _random = new Random();
    }

    /// <inheritdoc />
    public int Dimensions => _dimensions;

    /// <inheritdoc />
    public string ProviderName => "MockEmbedding";

    /// <inheritdoc />
    public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken ct = default)
    {
        var vector = GenerateMockVector(text);
        var tokenCount = EstimateTokenCount(text);
        return Task.FromResult(new EmbeddingResult(text, vector, tokenCount));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<EmbeddingResult>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default)
    {
        var results = texts.Select(text =>
        {
            var vector = GenerateMockVector(text);
            var tokenCount = EstimateTokenCount(text);
            return new EmbeddingResult(text, vector, tokenCount);
        }).ToList();

        return Task.FromResult<IReadOnlyList<EmbeddingResult>>(results);
    }

    /// <summary>
    /// 基于文本内容生成模拟向量
    /// 相似的文本会产生相似的向量
    /// </summary>
    private float[] GenerateMockVector(string text)
    {
        var vector = new float[_dimensions];
        
        // 使用文本哈希作为种子，确保相同文本产生相同向量
        var hash = ComputeStableHash(text);
        var seed = (int)(hash % int.MaxValue);
        var rng = new Random((int)seed);

        // 生成随机向量
        for (var i = 0; i < _dimensions; i++)
        {
            vector[i] = (float)(rng.NextDouble() * 2 - 1); // -1 to 1
        }

        // 归一化向量
        NormalizeVector(vector);

        // 添加基于关键词的偏移，使相似文本有更相似的向量
        AddKeywordInfluence(text, vector);

        // 再次归一化
        NormalizeVector(vector);

        return vector;
    }

    /// <summary>
    /// 添加关键词影响，使包含相似关键词的文本有更相似的向量
    /// </summary>
    private void AddKeywordInfluence(string text, float[] vector)
    {
        // 定义一些关键词组和它们影响的维度范围
        var keywordGroups = new Dictionary<string, (int StartDim, int Count, float Influence)>
        {
            { "machine learning", (0, 20, 0.3f) },
            { "artificial intelligence", (0, 20, 0.3f) },
            { "neural network", (20, 20, 0.3f) },
            { "deep learning", (20, 20, 0.3f) },
            { "algorithm", (40, 10, 0.2f) },
            { "data", (50, 10, 0.2f) },
            { "cook", (60, 10, 0.2f) },
            { "recipe", (60, 10, 0.2f) },
            { "food", (60, 10, 0.2f) },
        };

        var lowerText = text.ToLowerInvariant();

        foreach (var (keyword, (startDim, count, influence)) in keywordGroups)
        {
            if (lowerText.Contains(keyword))
            {
                for (var i = startDim; i < Math.Min(startDim + count, _dimensions); i++)
                {
                    vector[i] += influence;
                }
            }
        }
    }

    /// <summary>
    /// 归一化向量
    /// </summary>
    private static void NormalizeVector(float[] vector)
    {
        var magnitude = 0.0;
        for (var i = 0; i < vector.Length; i++)
        {
            magnitude += vector[i] * vector[i];
        }
        magnitude = Math.Sqrt(magnitude);

        if (magnitude > 0)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] = (float)(vector[i] / magnitude);
            }
        }
    }

    /// <summary>
    /// 计算稳定的哈希值
    /// </summary>
    private static long ComputeStableHash(string text)
    {
        unchecked
        {
            long hash = 17;
            foreach (var c in text)
            {
                hash = hash * 31 + c;
            }
            return Math.Abs(hash);
        }
    }

    /// <summary>
    /// 估算 Token 数量（简单实现）
    /// </summary>
    private static int EstimateTokenCount(string text)
    {
        // 简单估算：英文按空格分词，中文按字符计算
        var wordCount = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
        var chineseCount = text.Count(c => c > 127);
        return wordCount + chineseCount;
    }
}

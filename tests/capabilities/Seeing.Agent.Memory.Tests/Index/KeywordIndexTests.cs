using FluentAssertions;
using Microsoft.Data.Sqlite;
using Seeing.Agent.Memory.Core;
using Seeing.Agent.Memory.Core.Index;
using Seeing.Agent.Memory.Core.Models;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Index;

public class KeywordIndexTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly KeywordIndex _index;

    public KeywordIndexTests()
    {
        // 使用内存数据库进行测试
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _index = new KeywordIndex(_connection, new SqliteConnectionGate());
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
            Type: Seeing.Agent.Memory.Core.Models.MemoryType.Daily,
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
    public async Task IndexAsync_InsertsDocument()
    {
        // Arrange
        var node = CreateTestNode(
            "daily/2025-01-15.md",
            "# Test Document\n\nThis is a test document with some keywords.",
            title: "Test Document",
            tags: new[] { "test", "example" }
        );

        // Act
        await _index.IndexAsync(node);
        var count = await _index.GetDocumentCountAsync();

        // Assert
        count.Should().Be(1);
    }

    [Fact]
    public async Task SearchAsync_ReturnsRankedResults()
    {
        // Arrange
        var node1 = CreateTestNode(
            "daily/doc1.md",
            "Machine learning is a subset of artificial intelligence.",
            title: "Machine Learning Basics"
        );
        var node2 = CreateTestNode(
            "daily/doc2.md",
            "Artificial intelligence encompasses machine learning and more.",
            title: "AI Overview"
        );
        var node3 = CreateTestNode(
            "daily/doc3.md",
            "Cooking recipes for dinner.",
            title: "Cooking Guide"
        );

        await _index.IndexBatchAsync(new[] { node1, node2, node3 });

        // Act
        var results = await _index.SearchAsync("machine learning");

        // Assert
        results.Should().NotBeEmpty();
        results.Should().HaveCount(2); // doc1 and doc2 contain "machine learning"
        
        // 验证排序：doc1 应该排在前面，因为 "machine learning" 出现更直接
        results[0].Path.Should().Be("daily/doc1.md");
    }

    [Fact]
    public async Task SearchAsync_WithChineseKeywords()
    {
        // Arrange
        // 注意：FTS5 的 unicode61 tokenizer 对中文的支持有限
        // 它会按字符进行索引，但前缀匹配可以帮助找到包含中文关键词的结果
        var node1 = CreateTestNode(
            "daily/chinese1.md",
            "今天天气很好，适合出去散步。天气不错。",
            title: "日记"
        );
        var node2 = CreateTestNode(
            "daily/chinese2.md",
            "天气预报说明天可能会下雨。",
            title: "天气预报"
        );

        await _index.IndexBatchAsync(new[] { node1, node2 });

        // Act - 搜索"天气"
        var results = await _index.SearchAsync("天气");

        // Assert
        // 由于 FTS5 unicode61 对中文的 tokenizer 限制，前缀匹配可能只找到部分结果
        // 实际搜索行为取决于具体内容匹配
        results.Should().NotBeEmpty();
    }

    [Fact]
    public async Task RemoveAsync_DeletesDocument()
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
    public async Task SearchAsync_EmptyQuery_ReturnsEmptyResults()
    {
        // Arrange
        var node = CreateTestNode(
            "daily/test.md",
            "Some content",
            title: "Test"
        );
        await _index.IndexAsync(node);

        // Act
        var results = await _index.SearchAsync("");

        // Assert
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_NoMatch_ReturnsEmptyResults()
    {
        // Arrange
        var node = CreateTestNode(
            "daily/test.md",
            "Some unrelated content",
            title: "Test"
        );
        await _index.IndexAsync(node);

        // Act
        var results = await _index.SearchAsync("xyznonexistent123");

        // Assert
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task IndexAsync_UpdatesExistingDocument()
    {
        // Arrange
        var node1 = CreateTestNode(
            "daily/update.md",
            "Original content",
            title: "Original Title"
        );
        await _index.IndexAsync(node1);

        // Act - 更新同一文档
        var node2 = CreateTestNode(
            "daily/update.md",
            "Updated content with machine learning keywords",
            title: "Updated Title"
        );
        await _index.IndexAsync(node2);

        var count = await _index.GetDocumentCountAsync();
        var results = await _index.SearchAsync("machine learning");

        // Assert
        count.Should().Be(1); // 应该只有一份文档
        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Updated Title");
    }

    [Fact]
    public async Task IndexBatchAsync_InsertsMultipleDocuments()
    {
        // Arrange
        var nodes = new[]
        {
            CreateTestNode("daily/batch1.md", "Content 1", title: "Title 1"),
            CreateTestNode("daily/batch2.md", "Content 2", title: "Title 2"),
            CreateTestNode("daily/batch3.md", "Content 3", title: "Title 3"),
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
    public async Task SearchAsync_WithMultipleKeywords()
    {
        // Arrange
        var node1 = CreateTestNode(
            "daily/multi.md",
            "Machine learning algorithms for data science",
            title: "ML Guide"
        );
        var node2 = CreateTestNode(
            "daily/multi2.md",
            "Data science is a broad field",
            title: "DS Overview"
        );
        await _index.IndexBatchAsync(new[] { node1, node2 });

        // Act - 搜索多个关键词（AND 查询）
        var results = await _index.SearchAsync("machine learning data");

        // Assert
        results.Should().NotBeEmpty();
        // node1 包含所有关键词，应该排在前面
        results[0].Path.Should().Be("daily/multi.md");
    }

    [Fact]
    public async Task SearchAsync_SpecialCharactersAreHandled()
    {
        // Arrange
        var node = CreateTestNode(
            "daily/special.md",
            "Content with (parentheses) and [brackets]",
            title: "Special Characters"
        );
        await _index.IndexAsync(node);

        // Act - 搜索包含特殊字符的查询
        var results = await _index.SearchAsync("parentheses brackets");

        // Assert - 不应抛出异常，应正常返回结果
        results.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SearchAsync_TagsAreSearchable()
    {
        // Arrange
        var node = CreateTestNode(
            "daily/tagged.md",
            "Some content",
            title: "Tagged Document",
            tags: new[] { "important", "project-alpha", "review" }
        );
        await _index.IndexAsync(node);

        // Act
        var results = await _index.SearchAsync("project-alpha");

        // Assert
        results.Should().NotBeEmpty();
        results[0].Path.Should().Be("daily/tagged.md");
    }
}

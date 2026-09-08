using FluentAssertions;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core.Storage;
using System.IO;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Storage;

public class LocalFileStoreTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly LocalFileStore _store;

    public LocalFileStoreTests()
    {
        // 创建临时测试目录
        _testDirectory = Path.Combine(
            Path.GetTempPath(),
            "Seeing.Agent.Memory.Tests",
            Guid.NewGuid().ToString("N")[..8]
        );
        
        // 确保测试目录存在
        Directory.CreateDirectory(_testDirectory);
        
        _store = new LocalFileStore(_testDirectory);
    }

    public void Dispose()
    {
        _store.Dispose();
        
        // 清理测试目录
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

    [Fact]
    public async Task WriteAsync_CreatesFile_ReturnsNode()
    {
        // Arrange
        var path = "daily/2025-01-15/test-session.md";
        var content = @"---
id: test-123
type: daily
title: Test Session
tags:
  - test
  - example
importance: 0.8
confidence: 0.9
---

# Test Session

This is a test session with [[related/note]] link.
";

        // Act
        var result = await _store.WriteAsync(path, content);

        // Assert
        result.Should().NotBeNull();
        result.Path.Should().Be(path);
        result.Content.Should().Contain("# Test Session");
        result.Metadata.Id.Should().Be("test-123");
        result.Metadata.Type.Should().Be(Seeing.Agent.Memory.Core.Models.MemoryType.Daily);
        result.Metadata.Title.Should().Be("Test Session");
        result.Metadata.Tags.Should().Contain("test", "example");
        result.Metadata.Importance.Should().Be(0.8);
        result.Metadata.Confidence.Should().Be(0.9);
        result.Links.Should().Contain("related/note");

        // 文件应存在
        (await _store.ExistsAsync(path)).Should().BeTrue();
    }

    [Fact]
    public async Task WriteAsync_WithoutFrontMatter_CreatesDefaultMetadata()
    {
        // Arrange
        var path = "session/2025-01-15/raw-session.md";
        var content = "# Raw Session\n\nNo frontmatter here.";

        // Act
        var result = await _store.WriteAsync(path, content);

        // Assert
        result.Should().NotBeNull();
        result.Metadata.Id.Should().NotBeNullOrEmpty();
        result.Metadata.Type.Should().Be(Seeing.Agent.Memory.Core.Models.MemoryType.Session);
    }

    [Fact]
    public async Task ReadAsync_ExistingFile_ReturnsNode()
    {
        // Arrange
        var path = "digest/wiki/api-design.md";
        var content = @"---
id: api-design-001
type: digest
title: API Design Notes
---

# API Design

Best practices for REST API design.
";

        await _store.WriteAsync(path, content);

        // Act
        var result = await _store.ReadAsync(path);

        // Assert
        result.Should().NotBeNull();
        result!.Path.Should().Be(path);
        result.Content.Should().Contain("# API Design");
        result.Metadata.Id.Should().Be("api-design-001");
        result.Metadata.Type.Should().Be(Seeing.Agent.Memory.Core.Models.MemoryType.Digest);
        result.Metadata.Title.Should().Be("API Design Notes");
    }

    [Fact]
    public async Task ReadAsync_NonExistingFile_ReturnsNull()
    {
        // Arrange
        var path = "daily/nonexistent.md";

        // Act
        var result = await _store.ReadAsync(path);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_RemovesFile()
    {
        // Arrange
        var path = "session/temp-session.md";
        var content = "# Temporary";
        await _store.WriteAsync(path, content);
        (await _store.ExistsAsync(path)).Should().BeTrue();

        // Act
        await _store.DeleteAsync(path);

        // Assert
        (await _store.ExistsAsync(path)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_NonExistingFile_DoesNotThrow()
    {
        // Arrange
        var path = "daily/never-existed.md";

        // Act & Assert (should not throw)
        var act = async () => await _store.DeleteAsync(path);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ExistsAsync_ExistingFile_ReturnsTrue()
    {
        // Arrange
        var path = "daily/existing.md";
        await _store.WriteAsync(path, "# Content");

        // Act
        var result = await _store.ExistsAsync(path);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_NonExistingFile_ReturnsFalse()
    {
        // Arrange
        var path = "daily/nonexisting.md";

        // Act
        var result = await _store.ExistsAsync(path);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ListAsync_ReturnsAllFiles()
    {
        // Arrange
        await _store.WriteAsync("daily/2025-01-10/note1.md", "# Note 1");
        await _store.WriteAsync("daily/2025-01-11/note2.md", "# Note 2");
        await _store.WriteAsync("session/2025-01-12/session.md", "# Session");
        await _store.WriteAsync("digest/wiki/knowledge.md", "# Knowledge");

        // Act
        var result = await _store.ListAsync();

        // Assert
        result.Should().HaveCount(4);
        result.Select(f => f.Path).Should().Contain(
            "daily/2025-01-10/note1.md",
            "daily/2025-01-11/note2.md",
            "session/2025-01-12/session.md",
            "digest/wiki/knowledge.md"
        );
    }

    [Fact]
    public async Task ListAsync_WithPattern_ReturnsMatchingFiles()
    {
        // Arrange
        await _store.WriteAsync("daily/note-a.md", "# A");
        await _store.WriteAsync("daily/note-b.md", "# B");
        await _store.WriteAsync("session/session-x.md", "# X");

        // Act
        var result = await _store.ListAsync("note-*.md");

        // Assert
        result.Should().HaveCount(2);
        result.Select(f => f.Path).Should().Contain("daily/note-a.md", "daily/note-b.md");
    }

    [Fact]
    public async Task ListAsync_WithPathGlob_ShouldListByTypePrefix()
    {
        await _store.WriteAsync("daily/2025-01-10/note1.md", "# Note 1");
        await _store.WriteAsync("session/2025-01-10/session.md", "# Session");

        var result = await _store.ListAsync("daily/**/*.md");

        result.Should().HaveCount(1);
        result[0].Path.Should().Be("daily/2025-01-10/note1.md");
    }

    [Fact]
    public async Task ListByPrefixAsync_ReturnsMatchingFiles()
    {
        // Arrange
        await _store.WriteAsync("daily/2025-01-10/note1.md", "# Note 1");
        await _store.WriteAsync("daily/2025-01-11/note2.md", "# Note 2");
        await _store.WriteAsync("session/2025-01-10/session.md", "# Session");

        // Act
        var result = await _store.ListByPrefixAsync("daily");

        // Assert
        result.Should().HaveCount(2);
        result.All(f => f.Path.StartsWith("daily/")).Should().BeTrue();
    }

    [Fact]
    public async Task ListByPrefixAsync_NonExistingPrefix_ReturnsEmpty()
    {
        // Act
        var result = await _store.ListByPrefixAsync("nonexistent");

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task WriteBatchAsync_WritesMultipleFiles()
    {
        // Arrange
        var items = new[]
        {
            ("daily/batch-1.md", "# Batch 1"),
            ("daily/batch-2.md", "# Batch 2"),
            ("session/batch-3.md", "# Batch 3")
        };

        // Act
        await _store.WriteBatchAsync(items);

        // Assert
        (await _store.ExistsAsync("daily/batch-1.md")).Should().BeTrue();
        (await _store.ExistsAsync("daily/batch-2.md")).Should().BeTrue();
        (await _store.ExistsAsync("session/batch-3.md")).Should().BeTrue();
    }

    [Fact]
    public async Task WriteAsync_FiresCreatedEvent()
    {
        // Arrange
        var path = "daily/new-file.md";
        var eventFired = false;
        FileChangeEventArgs? eventArgs = null;

        using var subscription = _store.Changes.Subscribe(e =>
        {
            eventFired = true;
            eventArgs = e;
        });

        // Act
        await _store.WriteAsync(path, "# New Content");

        // Wait a bit for the event to propagate
        await Task.Delay(100);

        // Assert
        eventFired.Should().BeTrue();
        eventArgs.Should().NotBeNull();
        eventArgs!.Path.Should().Be(path);
        eventArgs.Type.Should().Be(FileChangeType.Created);
    }

    [Fact]
    public async Task WriteAsync_FiresModifiedEvent_WhenFileExists()
    {
        // Arrange
        var path = "daily/existing-for-modify.md";
        await _store.WriteAsync(path, "# Original");

        var eventFired = false;
        FileChangeEventArgs? eventArgs = null;

        using var subscription = _store.Changes.Subscribe(e =>
        {
            eventFired = true;
            eventArgs = e;
        });

        // Act - Write again to modify
        await _store.WriteAsync(path, "# Modified");

        // Wait a bit for the event to propagate
        await Task.Delay(100);

        // Assert
        eventFired.Should().BeTrue();
        eventArgs.Should().NotBeNull();
        eventArgs!.Path.Should().Be(path);
        eventArgs.Type.Should().Be(FileChangeType.Modified);
    }

    [Fact]
    public async Task DeleteAsync_FiresDeletedEvent()
    {
        // Arrange
        var path = "daily/to-delete.md";
        await _store.WriteAsync(path, "# To Delete");

        var eventFired = false;
        FileChangeEventArgs? eventArgs = null;

        using var subscription = _store.Changes.Subscribe(e =>
        {
            eventFired = true;
            eventArgs = e;
        });

        // Act
        await _store.DeleteAsync(path);

        // Wait a bit for the event to propagate
        await Task.Delay(100);

        // Assert
        eventFired.Should().BeTrue();
        eventArgs.Should().NotBeNull();
        eventArgs!.Path.Should().Be(path);
        eventArgs.Type.Should().Be(FileChangeType.Deleted);
    }

    [Fact]
    public async Task WriteAsync_InvalidPath_ThrowsArgumentException()
    {
        // Act & Assert
        var act = async () => await _store.WriteAsync("", "# Content");
        await act.Should().ThrowAsync<ArgumentException>();

        var act2 = async () => await _store.WriteAsync("../outside.md", "# Content");
        await act2.Should().ThrowAsync<ArgumentException>();

        var act3 = async () => await _store.WriteAsync("/absolute/path.md", "# Content");
        await act3.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ReadAsync_InvalidPath_ThrowsArgumentException()
    {
        // Act & Assert
        var act = async () => await _store.ReadAsync("");
        await act.Should().ThrowAsync<ArgumentException>();

        var act2 = async () => await _store.ReadAsync("../outside.md");
        await act2.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ReadAsync_ParsesWikilinks()
    {
        // Arrange
        var path = "daily/linked-note.md";
        var content = @"# Linked Note

See [[digest/wiki/api]] and [[daily/2025-01-10/related]].
Also check [[session/debug#anchor]].
";

        await _store.WriteAsync(path, content);

        // Act
        var result = await _store.ReadAsync(path);

        // Assert
        result.Should().NotBeNull();
        result!.Links.Should().HaveCount(3);
        result.Links.Should().Contain(
            "digest/wiki/api",
            "daily/2025-01-10/related",
            "session/debug"
        );
    }
}

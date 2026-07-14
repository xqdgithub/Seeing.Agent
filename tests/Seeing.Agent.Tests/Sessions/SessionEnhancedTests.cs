using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Agent.Tests.Sessions;

public class SessionDataEnhancedTests
{
    [Fact]
    public void SessionData_ForkFields_Defaults()
    {
        // Arrange & Act
        var session = new SessionData();

        // Assert
        session.ParentSessionId.Should().BeNull();
        session.ForkLabel.Should().BeNull();
        session.IsArchived.Should().BeFalse();
        session.ArchivedAt.Should().BeNull();
    }

    [Fact]
    public void SessionData_Clone_ShouldCopyForkFields()
    {
        // Arrange
        var session = new SessionData
        {
            Id = "test-id",
            ParentSessionId = "parent-id",
            ForkLabel = "Test Fork",
            IsArchived = true,
            ArchivedAt = DateTimeOffset.UtcNow
        };

        // Act
        var clone = session.Clone();

        // Assert
        clone.ParentSessionId.Should().Be("parent-id");
        clone.ForkLabel.Should().Be("Test Fork");
        clone.IsArchived.Should().BeTrue();
        clone.ArchivedAt.Should().NotBeNull();
    }
}

public class SessionMetadataTests
{
    [Fact]
    public void SessionMetadata_Defaults()
    {
        // Arrange & Act
        var metadata = new SessionMetadata();

        // Assert
        metadata.Id.Should().BeEmpty();
        metadata.IsArchived.Should().BeFalse();
        metadata.MessageCount.Should().Be(0);
    }
}

public class SessionForkerTests
{
    [Fact]
    public async Task ForkAsync_ShouldCreateNewSession()
    {
        // Arrange
        var (manager, forker) = CreateForker();
        var original = manager.Create();
        original.AddMessage(new SessionMessage { Role = "user", Content = "Hello" });

        // Act
        var forked = await forker.ForkAsync(original.Id);

        // Assert
        forked.Id.Should().NotBe(original.Id);
        forked.ParentSessionId.Should().Be(original.Id);
        forked.Messages.Should().HaveCount(1);
    }

    [Fact]
    public async Task ForkAsync_WithMessageId_ShouldTruncate()
    {
        // Arrange
        var (manager, forker) = CreateForker();
        var original = manager.Create();
        original.AddMessage(new SessionMessage { Id = "msg1", Role = "user", Content = "Hello" });
        original.AddMessage(new SessionMessage { Id = "msg2", Role = "assistant", Content = "Hi" });

        // Act - Fork at msg1 (should not include msg1)
        var forked = await forker.ForkAsync(original.Id, "msg1");

        // Assert
        forked.Messages.Should().BeEmpty();
    }

    private static (SessionManager, SessionForker) CreateForker()
    {
        var logger = new Mock<ILogger<SessionManager>>();
        var forkerLogger = new Mock<ILogger<SessionForker>>();
        var store = new InMemorySessionStore();
        var manager = new SessionManager(store, logger: logger.Object);
        var forker = new SessionForker(forkerLogger.Object, manager);
        return (manager, forker);
    }
}

public class SessionArchiverTests
{
    [Fact]
    public async Task ArchiveAsync_ShouldMarkAsArchived()
    {
        // Arrange
        var logger = new Mock<ILogger<SessionArchiver>>();
        var tempPath = Path.Combine(Path.GetTempPath(), $"archive-{Guid.NewGuid():N}");
        var archiver = new SessionArchiver(logger.Object, tempPath);
        var session = SessionData.Create();
        session.AddMessage(new SessionMessage { Role = "user", Content = "Test" });

        // Act
        var result = await archiver.ArchiveAsync(session);

        // Assert
        result.Should().BeTrue();
        session.IsArchived.Should().BeTrue();
        session.ArchivedAt.Should().NotBeNull();
        session.Status.Should().Be(SessionStatus.Archived);
    }

    [Fact]
    public void ListArchives_ShouldReturnArchives()
    {
        // Arrange
        var logger = new Mock<ILogger<SessionArchiver>>();
        var tempPath = Path.Combine(Path.GetTempPath(), $"archive-{Guid.NewGuid():N}");
        var archiver = new SessionArchiver(logger.Object, tempPath);

        // Act
        var archives = archiver.ListArchives();

        // Assert
        archives.Should().NotBeNull();
    }
}

public class SessionSharerTests
{
    [Fact]
    public async Task ShareAsync_ShouldGenerateShareId()
    {
        // Arrange
        var logger = new Mock<ILogger<SessionSharer>>();
        var tempPath = Path.Combine(Path.GetTempPath(), $"shares-{Guid.NewGuid():N}");
        var sharer = new SessionSharer(logger.Object, tempPath);
        var session = SessionData.Create();

        // Act
        var shareId = await sharer.ShareAsync(session);

        // Assert
        shareId.Should().StartWith("session://share/");
    }

    [Fact]
    public async Task ResolveAsync_ValidShare_ShouldReturnSession()
    {
        // Arrange
        var logger = new Mock<ILogger<SessionSharer>>();
        var tempPath = Path.Combine(Path.GetTempPath(), $"shares-{Guid.NewGuid():N}");
        var sharer = new SessionSharer(logger.Object, tempPath);
        var session = SessionData.Create();
        session.AddMessage(new SessionMessage { Role = "user", Content = "Test" });
        var shareId = await sharer.ShareAsync(session);
        var shareGuid = shareId.Replace("session://share/", "");

        // Act
        var resolved = await sharer.ResolveAsync(shareGuid);

        // Assert
        resolved.Should().NotBeNull();
        resolved!.Id.Should().Be(session.Id);
    }
}

public class SessionReverterTests
{
    [Fact]
    public async Task RevertAsync_ShouldTruncateMessages()
    {
        // Arrange
        var (manager, reverter) = CreateReverter();
        var session = manager.Create();
        session.AddMessage(new SessionMessage { Id = "msg1", Role = "user", Content = "Hello" });
        session.AddMessage(new SessionMessage { Id = "msg2", Role = "assistant", Content = "Hi" });
        session.AddMessage(new SessionMessage { Id = "msg3", Role = "user", Content = "Bye" });

        // Act
        var result = await reverter.RevertAsync(session.Id, "msg2");

        // Assert
        result.Should().BeTrue();
        session.Messages.Should().HaveCount(2); // msg1 + msg2
    }

    [Fact]
    public async Task RevertToLastUserMessageAsync_ShouldWork()
    {
        // Arrange
        var (manager, reverter) = CreateReverter();
        var session = manager.Create();
        session.AddMessage(new SessionMessage { Id = "msg1", Role = "user", Content = "Hello" });
        session.AddMessage(new SessionMessage { Id = "msg2", Role = "assistant", Content = "Hi" });
        session.AddMessage(new SessionMessage { Id = "msg3", Role = "user", Content = "Bye" });
        session.AddMessage(new SessionMessage { Id = "msg4", Role = "assistant", Content = "Goodbye" });

        // Act
        var result = await reverter.RevertToLastUserMessageAsync(session.Id);

        // Assert
        result.Should().BeTrue();
        session.Messages.Should().HaveCount(3); // msg1, msg2, msg3 (up to and including last user message)
    }

    private static (SessionManager, SessionReverter) CreateReverter()
    {
        var logger = new Mock<ILogger<SessionManager>>();
        var reverterLogger = new Mock<ILogger<SessionReverter>>();
        var store = new InMemorySessionStore();
        var manager = new SessionManager(store, logger: logger.Object);
        var reverter = new SessionReverter(reverterLogger.Object, manager);
        return (manager, reverter);
    }
}

public class GlobalSessionStoreTests
{
    [Fact]
    public async Task ListAllAsync_EmptyDirectory_ShouldReturnEmpty()
    {
        // Arrange
        var logger = new Mock<ILogger<GlobalSessionStore>>();
        var tempPath = Path.Combine(Path.GetTempPath(), $"sessions-{Guid.NewGuid():N}");
        var store = new GlobalSessionStore(logger.Object, tempPath);

        // Act
        var sessions = await store.ListAllAsync();

        // Assert
        sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task GetStatisticsAsync_ShouldReturnStats()
    {
        // Arrange
        var logger = new Mock<ILogger<GlobalSessionStore>>();
        var tempPath = Path.Combine(Path.GetTempPath(), $"sessions-{Guid.NewGuid():N}");
        var store = new GlobalSessionStore(logger.Object, tempPath);

        // Act
        var stats = await store.GetStatisticsAsync();

        // Assert
        stats.Should().NotBeNull();
        stats.PartitionCount.Should().Be(0);
        stats.TotalSessions.Should().Be(0);
    }
}

using FluentAssertions;
using Seeing.Session.Core;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Session.Tests.Storage;

public class SessionSummaryAndArchiveTests
{
    private static string NewTempDir() =>
        Path.Combine(Path.GetTempPath(), "seeing-archive-test-" + Guid.NewGuid().ToString("N"));

    private static async Task<SessionData> SeedAsync(FileSessionStore store, string title = "t", int messageCount = 3)
    {
        var session = SessionData.Create(partitionId: "default", selectedAgent: "build");
        session.Title = title;
        for (var i = 0; i < messageCount; i++)
            session.AddMessage(new SessionMessage { Role = MessageRole.User, Content = $"m{i}" });

        await store.SaveAsync(session);
        return session;
    }

    [Fact]
    public async Task ListSummariesAsync_ShouldReturnMetadataWithoutMessages()
    {
        var dir = NewTempDir();
        try
        {
            var store = new FileSessionStore(dir);
            var session = await SeedAsync(store, "hello", 5);

            var summaries = new List<SessionSummary>();
            await foreach (var s in store.ListSummariesAsync())
                summaries.Add(s);

            summaries.Should().ContainSingle();
            var summary = summaries[0];
            summary.Id.Should().Be(session.Id);
            summary.Title.Should().Be("hello");
            summary.SelectedAgent.Should().Be("build");
            summary.MessageCount.Should().Be(5);
            summary.IsArchived.Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ArchiveAsync_ShouldMoveToArchiveAndHideFromSummaries()
    {
        var dir = NewTempDir();
        try
        {
            var store = new FileSessionStore(dir);
            var session = await SeedAsync(store);

            await store.ArchiveAsync(session.Id);

            // 活跃目录无该文件
            File.Exists(Path.Combine(dir, $"{session.Id}.json")).Should().BeFalse();

            // 摘要不再包含
            var active = new List<SessionSummary>();
            await foreach (var s in store.ListSummariesAsync())
                active.Add(s);
            active.Should().BeEmpty();

            // 归档列表包含
            var archived = new List<SessionSummary>();
            await foreach (var s in store.ListArchivedAsync())
                archived.Add(s);
            archived.Should().ContainSingle();
            archived[0].Id.Should().Be(session.Id);
            archived[0].IsArchived.Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAsync_ShouldBringSessionBack()
    {
        var dir = NewTempDir();
        try
        {
            var store = new FileSessionStore(dir);
            var session = await SeedAsync(store, "restore-me", 4);

            await store.ArchiveAsync(session.Id);
            var restored = await store.RestoreAsync(session.Id);

            restored.Should().BeTrue();
            File.Exists(Path.Combine(dir, $"{session.Id}.json")).Should().BeTrue();

            var loaded = await store.LoadAsync(session.Id);
            loaded.Should().NotBeNull();
            loaded!.IsArchived.Should().BeFalse();
            loaded.MessageCount.Should().Be(4);
            loaded.Title.Should().Be("restore-me");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ArchiveThenRestore_ShouldPreserveOriginalStatus()
    {
        var dir = NewTempDir();
        try
        {
            var store = new FileSessionStore(dir);
            var session = SessionData.Create();
            session.Status = SessionStatus.Completed;
            await store.SaveAsync(session);

            await store.ArchiveAsync(session.Id);
            (await store.LoadAsync(session.Id)).Should().BeNull();

            await store.RestoreAsync(session.Id);
            var loaded = await store.LoadAsync(session.Id);

            loaded!.Status.Should().Be(SessionStatus.Completed);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ArchiveAsync_ShouldReturnFalse_WhenSessionMissing()
    {
        var dir = NewTempDir();
        try
        {
            var store = new FileSessionStore(dir);
            (await store.ArchiveAsync("ses_missing")).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task InMemoryStore_ShouldSupportSummaryAndArchive()
    {
        var store = new InMemorySessionStore();
        var session = SessionData.Create(selectedAgent: "plan");
        session.AddMessage(new SessionMessage { Role = MessageRole.User, Content = "x" });
        await store.SaveAsync(session);

        var summaries = new List<SessionSummary>();
        await foreach (var s in store.ListSummariesAsync())
            summaries.Add(s);
        summaries.Should().ContainSingle();
        summaries[0].MessageCount.Should().Be(1);

        await store.ArchiveAsync(session.Id);
        (await store.LoadAsync(session.Id)).Should().BeNull();

        var archived = new List<SessionSummary>();
        await foreach (var s in store.ListArchivedAsync())
            archived.Add(s);
        archived.Should().ContainSingle();

        (await store.RestoreAsync(session.Id)).Should().BeTrue();
        (await store.LoadAsync(session.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task ListSummariesAsync_ShouldFallbackForStoreWithoutProvider()
    {
        var store = new MinimalStore();
        var session = SessionData.Create(selectedAgent: "build");
        session.Title = "fallback";
        session.AddMessage(new SessionMessage { Role = MessageRole.User, Content = "y" });
        await store.SaveAsync(session);

        var summaries = new List<SessionSummary>();
        await foreach (var s in store.ListSummariesAsync())
            summaries.Add(s);

        summaries.Should().ContainSingle();
        summaries[0].Title.Should().Be("fallback");
        summaries[0].MessageCount.Should().Be(1);
        store.SupportsArchive().Should().BeFalse();
    }

    [Fact]
    public async Task ArchiveAsync_ShouldThrow_WhenBackendDoesNotSupportArchive()
    {
        var store = new MinimalStore();

        var act = async () => await store.ArchiveAsync("ses_x");
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    private sealed class MinimalStore : ISessionStore
    {
        private readonly Dictionary<string, SessionData> _map = new();

        public Task SaveAsync(SessionData data, CancellationToken ct = default)
        {
            _map[data.Id] = data;
            return Task.CompletedTask;
        }

        public Task<SessionData?> LoadAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(_map.TryGetValue(sessionId, out var s) ? s : null);

        public Task DeleteAsync(string sessionId, CancellationToken ct = default)
        {
            _map.Remove(sessionId);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<SessionData> ListAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            foreach (var s in _map.Values) yield return s;
        }

        public IAsyncEnumerable<SessionData> QueryAsync(string partitionId, string agentId, CancellationToken ct = default) =>
            ListAsync(ct);

        public Task SaveAllAsync(IEnumerable<SessionData> data, CancellationToken ct = default) =>
            Task.CompletedTask;

        public IAsyncEnumerable<SessionData> LoadAllAsync(CancellationToken ct = default) =>
            ListAsync(ct);
    }
}

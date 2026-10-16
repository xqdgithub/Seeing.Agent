using FluentAssertions;
using Seeing.Session.Core;
using Seeing.Session.Persistence;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Session.Tests.Persistence;

/// <summary>
/// 写回装饰器对新端口（摘要 / 归档）的转发与能力探测语义。
/// </summary>
public sealed class WriteBehindSessionArchiveTests
{
    private static WriteBehindSessionStore Create(ISessionStore inner) => new(
        inner,
        new SessionPersistenceOptions
        {
            DebounceWindow = TimeSpan.FromSeconds(10),
            MaxFlushDelay = TimeSpan.FromSeconds(30),
            MaxRetryBackoff = TimeSpan.FromMilliseconds(20),
            ShutdownFlushTimeout = TimeSpan.FromSeconds(2),
            ReadFlushTimeout = TimeSpan.FromMilliseconds(100)
        });

    [Fact]
    public async Task ListSummariesAsync_ShouldFlushPendingBeforeRead()
    {
        var inner = new InMemorySessionStore();
        using var store = Create(inner);

        await store.SaveAsync(new SessionData { Id = "s1", Title = "A" }, TestContext.Current.CancellationToken);

        var summaries = new List<SessionSummary>();
        await foreach (var s in store.ListSummariesAsync(TestContext.Current.CancellationToken))
            summaries.Add(s);

        summaries.Should().ContainSingle(s => s.Id == "s1" && s.Title == "A");
    }

    [Fact]
    public async Task ArchiveAsync_ShouldDelegateAndHideFromSummaries()
    {
        var inner = new InMemorySessionStore();
        using var store = Create(inner);

        await store.SaveAsync(new SessionData { Id = "s1", Title = "A" }, TestContext.Current.CancellationToken);
        await store.FlushAllAsync(TestContext.Current.CancellationToken);

        var archived = await store.ArchiveAsync("s1", TestContext.Current.CancellationToken);

        archived.Should().BeTrue();
        (await store.LoadAsync("s1", TestContext.Current.CancellationToken)).Should().BeNull();

        var summaries = new List<SessionSummary>();
        await foreach (var s in store.ListSummariesAsync(TestContext.Current.CancellationToken))
            summaries.Add(s);
        summaries.Should().BeEmpty();

        var restored = await store.RestoreAsync("s1", TestContext.Current.CancellationToken);
        restored.Should().BeTrue();
        (await store.LoadAsync("s1", TestContext.Current.CancellationToken)).Should().NotBeNull();
    }

    [Fact]
    public void SupportsArchive_WhenInnerSupports_ShouldBeTrue()
    {
        using var store = Create(new InMemorySessionStore());
        store.SupportsArchive().Should().BeTrue();
        store.InnerSupportsArchive.Should().BeTrue();
    }

    [Fact]
    public void SupportsArchive_WhenInnerLacksArchive_ShouldBeFalse()
    {
        using var store = Create(new MinimalStore());
        store.SupportsArchive().Should().BeFalse();
        store.InnerSupportsArchive.Should().BeFalse();
    }

    [Fact]
    public async Task ArchiveAsync_ShouldFlushLatestSnapshotBeforeArchiving()
    {
        var inner = new OrderRecordingStore();
        using var store = Create(inner);

        // 入队较新快照（标题 v2），不主动 flush
        await store.SaveAsync(new SessionData { Id = "s1", Title = "v2" }, TestContext.Current.CancellationToken);

        await store.ArchiveAsync("s1", TestContext.Current.CancellationToken);

        // 归档前必须先把最新快照落盘，再归档；顺序：save → archive
        inner.Events.Should().ContainInOrder("save:s1:v2", "archive:s1");
        inner.ArchivedTitle.Should().Be("v2");
    }

    [Fact]
    public async Task ArchiveAsync_WhenInnerLacksArchive_ShouldThrowWithoutDiscardingPending()
    {
        var inner = new MinimalStore();
        using var store = Create(inner);

        await store.SaveAsync(new SessionData { Id = "s1", Title = "keep" }, TestContext.Current.CancellationToken);

        var act = async () => await store.ArchiveAsync("s1", TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<NotSupportedException>();

        // 能力检查前置：待写未被丢弃，flush 后仍能落盘
        await store.FlushAllAsync(TestContext.Current.CancellationToken);
        inner.SavedTitles.Should().Contain("keep");
    }

    private sealed class OrderRecordingStore : ISessionStore, ISessionArchiveStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, SessionData> _saved = new(StringComparer.Ordinal);
        private readonly List<string> _events = new();

        public string? ArchivedTitle { get; private set; }

        public IReadOnlyList<string> Events
        {
            get { lock (_gate) return _events.ToArray(); }
        }

        public Task SaveAsync(SessionData data, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _saved[data.Id] = data;
                _events.Add($"save:{data.Id}:{data.Title}");
            }
            return Task.CompletedTask;
        }

        public Task<SessionData?> LoadAsync(string sessionId, CancellationToken ct = default)
        {
            lock (_gate)
                return Task.FromResult(_saved.TryGetValue(sessionId, out var s) ? s : null);
        }

        public Task DeleteAsync(string sessionId, CancellationToken ct = default)
        {
            lock (_gate) _saved.Remove(sessionId);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<SessionData> ListAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            List<SessionData> snapshot;
            lock (_gate) snapshot = _saved.Values.ToList();
            foreach (var s in snapshot) yield return s;
        }

        public IAsyncEnumerable<SessionData> QueryAsync(string partitionId, string agentId, CancellationToken ct = default) =>
            ListAsync(ct);

        public Task SaveAllAsync(IEnumerable<SessionData> data, CancellationToken ct = default) => Task.CompletedTask;

        public IAsyncEnumerable<SessionData> LoadAllAsync(CancellationToken ct = default) => ListAsync(ct);

        public Task<bool> ArchiveAsync(string sessionId, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (_saved.TryGetValue(sessionId, out var s))
                    ArchivedTitle = s.Title;
                _saved.Remove(sessionId);
                _events.Add($"archive:{sessionId}");
            }
            return Task.FromResult(true);
        }

        public Task<bool> RestoreAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(false);

        public async IAsyncEnumerable<SessionSummary> ListArchivedAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class MinimalStore : ISessionStore
    {
        private readonly List<string> _savedTitles = new();

        public IReadOnlyList<string> SavedTitles
        {
            get { lock (_savedTitles) return _savedTitles.ToArray(); }
        }

        public Task SaveAsync(SessionData data, CancellationToken ct = default)
        {
            lock (_savedTitles) _savedTitles.Add(data.Title);
            return Task.CompletedTask;
        }

        public Task<SessionData?> LoadAsync(string sessionId, CancellationToken ct = default) => Task.FromResult<SessionData?>(null);
        public Task DeleteAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;

        public async IAsyncEnumerable<SessionData> ListAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public IAsyncEnumerable<SessionData> QueryAsync(string partitionId, string agentId, CancellationToken ct = default) =>
            ListAsync(ct);

        public Task SaveAllAsync(IEnumerable<SessionData> data, CancellationToken ct = default) => Task.CompletedTask;

        public IAsyncEnumerable<SessionData> LoadAllAsync(CancellationToken ct = default) => ListAsync(ct);
    }
}

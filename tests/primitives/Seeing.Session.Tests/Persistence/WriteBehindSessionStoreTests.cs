using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Seeing.Session.Core;
using Seeing.Session.Persistence;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Session.Tests.Persistence;

/// <summary>
/// <see cref="WriteBehindSessionStore"/> 装饰器语义测试（设计规格 §5.1 / §9）。
/// 使用短去抖窗口与短超时，避免长时间等待。
/// </summary>
public sealed class WriteBehindSessionStoreTests
{
    [Fact]
    public async Task SaveAsync_WithinWindow_ShouldCoalesceToSingleWriteWithLastValue()
    {
        var inner = new RecordingSessionStore();
        using var store = Create(inner, debounce: TimeSpan.FromSeconds(10));

        for (var i = 0; i < 10; i++)
            await store.SaveAsync(new SessionData { Id = "s1", Title = $"v{i}" }, TestContext.Current.CancellationToken);

        await store.FlushAllAsync(TestContext.Current.CancellationToken);

        inner.SaveCount.Should().Be(1);
        inner.LastSaved!.Title.Should().Be("v9");
    }

    [Fact]
    public async Task SaveAsync_ShouldEnqueueSnapshotWithoutCloning()
    {
        var inner = new RecordingSessionStore();
        using var store = Create(inner, debounce: TimeSpan.FromSeconds(10));

        var data = new SessionData { Id = "s1", Title = "A" };
        await store.SaveAsync(data, TestContext.Current.CancellationToken);
        await store.FlushAllAsync(TestContext.Current.CancellationToken);

        inner.LastSaved.Should().BeSameAs(data);
    }

    [Fact]
    public async Task LoadAsync_ShouldFlushPendingBeforeReading()
    {
        var inner = new RecordingSessionStore();
        using var store = Create(inner, debounce: TimeSpan.FromSeconds(10));

        await store.SaveAsync(new SessionData { Id = "s1", Title = "A" }, TestContext.Current.CancellationToken);

        var loaded = await store.LoadAsync("s1", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        loaded!.Title.Should().Be("A");
        inner.IndexOfEvent("save:s1").Should().BeLessThan(inner.IndexOfEvent("load:s1"));
    }

    [Fact]
    public async Task LoadAsync_WhenInnerWriteKeepsFailing_ShouldNotHang()
    {
        var inner = new RecordingSessionStore
        {
            SaveInterceptor = (_, _) => Task.FromException(new IOException("持续失败"))
        };
        using var store = Create(
            inner,
            debounce: TimeSpan.FromMilliseconds(20),
            readFlush: TimeSpan.FromMilliseconds(80),
            shutdown: TimeSpan.FromMilliseconds(200));

        await store.SaveAsync(new SessionData { Id = "s1", Title = "A" }, TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();
        var loaded = await store.LoadAsync("s1", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        stopwatch.Stop();

        loaded.Should().BeNull();
        inner.SaveCount.Should().Be(0);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DeleteAsync_AfterPendingSave_ShouldDiscardAndNotResurrect()
    {
        var inner = new RecordingSessionStore();
        using var store = Create(inner, debounce: TimeSpan.FromSeconds(10));

        await store.SaveAsync(new SessionData { Id = "s1", Title = "A" }, TestContext.Current.CancellationToken);
        await store.DeleteAsync("s1", TestContext.Current.CancellationToken);

        await Task.Delay(150, TestContext.Current.CancellationToken);

        inner.DeleteCount.Should().Be(1);
        inner.SaveCount.Should().Be(0);
        (await inner.LoadAsync("s1", TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_ShouldFlushBeforeDelegate()
    {
        var inner = new RecordingSessionStore();
        using var store = Create(inner, debounce: TimeSpan.FromSeconds(10));

        await store.SaveAsync(new SessionData { Id = "s1", Title = "A" }, TestContext.Current.CancellationToken);

        var list = await store.ListAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        list.Should().ContainSingle();
        inner.IndexOfEvent("save:s1").Should().BeLessThan(inner.IndexOfEvent("list"));
    }

    [Fact]
    public async Task QueryAsync_ShouldFlushBeforeDelegate()
    {
        var inner = new RecordingSessionStore();
        using var store = Create(inner, debounce: TimeSpan.FromSeconds(10));

        await store.SaveAsync(new SessionData { Id = "s1", Title = "A", PartitionId = "p1", SelectedAgent = "build" }, TestContext.Current.CancellationToken);

        var list = await store.QueryAsync("p1", "build", TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        list.Should().ContainSingle();
        inner.IndexOfEvent("save:s1").Should().BeLessThan(inner.IndexOfEvent("query"));
    }

    [Fact]
    public async Task LoadAllAsync_ShouldFlushBeforeDelegate()
    {
        var inner = new RecordingSessionStore();
        using var store = Create(inner, debounce: TimeSpan.FromSeconds(10));

        await store.SaveAsync(new SessionData { Id = "s1", Title = "A" }, TestContext.Current.CancellationToken);

        var list = await store.LoadAllAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        list.Should().ContainSingle();
        inner.IndexOfEvent("save:s1").Should().BeLessThan(inner.IndexOfEvent("loadall"));
    }

    [Fact]
    public async Task SaveAllAsync_ShouldFlushImmediately()
    {
        var inner = new RecordingSessionStore();
        using var store = Create(inner, debounce: TimeSpan.FromSeconds(10));

        await store.SaveAllAsync(
        [
            new SessionData { Id = "s1", Title = "A" },
            new SessionData { Id = "s2", Title = "B" }
        ], TestContext.Current.CancellationToken);

        inner.SaveCount.Should().Be(2);
        (await inner.LoadAsync("s1", TestContext.Current.CancellationToken)).Should().NotBeNull();
        (await inner.LoadAsync("s2", TestContext.Current.CancellationToken)).Should().NotBeNull();
    }

    [Fact]
    public async Task FlushAsync_ShouldWaitForWriteCompletion()
    {
        var inner = new RecordingSessionStore();
        using var store = Create(inner, debounce: TimeSpan.FromSeconds(10));

        await store.SaveAsync(new SessionData { Id = "s1", Title = "A" }, TestContext.Current.CancellationToken);
        await store.FlushAsync("s1", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        inner.SaveCount.Should().Be(1);
        inner.LastSaved!.Title.Should().Be("A");
    }

    [Fact]
    public async Task Relocatable_ShouldForwardBaseDirectoryAndSetBaseDirectory()
    {
        var inner = new RelocatableRecordingSessionStore { BaseDirectory = "C:\\ws1" };
        using var store = Create(inner);

        store.BaseDirectory.Should().Be("C:\\ws1");

        store.SetBaseDirectory("C:\\ws2");

        inner.BaseDirectory.Should().Be("C:\\ws2");
        store.BaseDirectory.Should().Be("C:\\ws2");
    }

    [Fact]
    public void Relocatable_WhenInnerNotRelocatable_ShouldBeNullAndNoOp()
    {
        var inner = new RecordingSessionStore();
        using var store = Create(inner);

        store.BaseDirectory.Should().BeNull();

        var act = () => store.SetBaseDirectory("C:\\ws2");
        act.Should().NotThrow();
    }

    [Fact]
    public async Task DisposeAsync_ShouldFlushPendingWrites()
    {
        var inner = new RecordingSessionStore();
        var store = Create(inner, debounce: TimeSpan.FromSeconds(10));

        await store.SaveAsync(new SessionData { Id = "s1", Title = "A" }, TestContext.Current.CancellationToken);
        await store.DisposeAsync();

        inner.SaveCount.Should().Be(1);
        inner.LastSaved!.Title.Should().Be("A");
    }

    [Fact]
    public async Task Dispose_ShouldFlushPendingWrites()
    {
        var inner = new RecordingSessionStore();
        var store = Create(inner, debounce: TimeSpan.FromSeconds(10));

        await store.SaveAsync(new SessionData { Id = "s1", Title = "A" }, TestContext.Current.CancellationToken);
        store.Dispose();

        inner.SaveCount.Should().Be(1);
    }

    private static WriteBehindSessionStore Create(
        ISessionStore inner,
        TimeSpan? debounce = null,
        TimeSpan? readFlush = null,
        TimeSpan? shutdown = null)
        => new(
            inner,
            new SessionPersistenceOptions
            {
                DebounceWindow = debounce ?? TimeSpan.FromSeconds(10),
                MaxFlushDelay = TimeSpan.FromSeconds(30),
                MaxRetryBackoff = TimeSpan.FromMilliseconds(20),
                ShutdownFlushTimeout = shutdown ?? TimeSpan.FromSeconds(2),
                ReadFlushTimeout = readFlush ?? TimeSpan.FromMilliseconds(100)
            });

    /// <summary>记录调用次数、顺序与最后写入对象的内存存储实现。</summary>
    private class RecordingSessionStore : ISessionStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, SessionData> _saved = new(StringComparer.Ordinal);
        private readonly List<string> _events = new();

        /// <summary>可选保存拦截器（抛异常以模拟持续写失败）。</summary>
        public Func<SessionData, CancellationToken, Task>? SaveInterceptor { get; set; }

        public int SaveCount { get; private set; }

        public int LoadCount { get; private set; }

        public int DeleteCount { get; private set; }

        public int ListCount { get; private set; }

        public int QueryCount { get; private set; }

        public int LoadAllCount { get; private set; }

        public SessionData? LastSaved { get; private set; }

        public IReadOnlyList<string> Events
        {
            get
            {
                lock (_gate)
                    return _events.ToArray();
            }
        }

        public int IndexOfEvent(string eventName)
        {
            lock (_gate)
                return _events.IndexOf(eventName);
        }

        public async Task SaveAsync(SessionData data, CancellationToken ct = default)
        {
            if (SaveInterceptor is not null)
                await SaveInterceptor(data, ct).ConfigureAwait(false);

            lock (_gate)
            {
                SaveCount++;
                LastSaved = data;
                _saved[data.Id] = data;
                _events.Add("save:" + data.Id);
            }
        }

        public Task<SessionData?> LoadAsync(string sessionId, CancellationToken ct = default)
        {
            lock (_gate)
            {
                LoadCount++;
                _events.Add("load:" + sessionId);
                return Task.FromResult(_saved.TryGetValue(sessionId, out var data) ? data : null);
            }
        }

        public Task DeleteAsync(string sessionId, CancellationToken ct = default)
        {
            lock (_gate)
            {
                DeleteCount++;
                _saved.Remove(sessionId);
                _events.Add("delete:" + sessionId);
            }

            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<SessionData> ListAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            List<SessionData> snapshot;
            lock (_gate)
            {
                ListCount++;
                _events.Add("list");
                snapshot = _saved.Values.ToList();
            }

            foreach (var item in snapshot)
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
            }

            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<SessionData> QueryAsync(
            string partitionId,
            string agentId,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            List<SessionData> snapshot;
            lock (_gate)
            {
                QueryCount++;
                _events.Add("query");
                snapshot = _saved.Values
                    .Where(d => d.PartitionId == partitionId && d.SelectedAgent == agentId)
                    .ToList();
            }

            foreach (var item in snapshot)
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
            }

            await Task.CompletedTask;
        }

        public async Task SaveAllAsync(IEnumerable<SessionData> data, CancellationToken ct = default)
        {
            foreach (var item in data)
                await SaveAsync(item, ct).ConfigureAwait(false);
        }

        public async IAsyncEnumerable<SessionData> LoadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            List<SessionData> snapshot;
            lock (_gate)
            {
                LoadAllCount++;
                _events.Add("loadall");
                snapshot = _saved.Values.ToList();
            }

            foreach (var item in snapshot)
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
            }

            await Task.CompletedTask;
        }
    }

    /// <summary>支持重定位能力的内层存储。</summary>
    private sealed class RelocatableRecordingSessionStore : RecordingSessionStore, IRelocatableSessionStore
    {
        public string? BaseDirectory { get; set; } = "C:\\workspace-initial";

        public void SetBaseDirectory(string baseDirectory) => BaseDirectory = baseDirectory;
    }
}

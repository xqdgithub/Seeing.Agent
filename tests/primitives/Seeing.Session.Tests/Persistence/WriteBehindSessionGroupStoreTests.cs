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
/// <see cref="WriteBehindSessionGroupStore"/> 装饰器语义测试（设计规格 §5.2 / §9）。
/// 使用短去抖窗口与短超时，避免长时间等待。
/// </summary>
public sealed class WriteBehindSessionGroupStoreTests
{
    [Fact]
    public async Task SaveAsync_WithinWindow_ShouldCoalesceToSingleWriteWithLastValue()
    {
        var inner = new RecordingGroupStore();
        using var store = Create(inner, debounce: TimeSpan.FromSeconds(10));

        for (var i = 0; i < 10; i++)
            await store.SaveAsync(new SessionGroup { Id = "g1", Title = $"v{i}" }, TestContext.Current.CancellationToken);

        await store.FlushAllAsync(TestContext.Current.CancellationToken);

        inner.SaveCount.Should().Be(1);
        inner.LastSaved!.Title.Should().Be("v9");
    }

    [Fact]
    public async Task SaveAsync_ShouldEnqueueSnapshotWithoutCloning()
    {
        var inner = new RecordingGroupStore();
        using var store = Create(inner, debounce: TimeSpan.FromSeconds(10));

        var group = new SessionGroup { Id = "g1", Title = "A" };
        await store.SaveAsync(group, TestContext.Current.CancellationToken);
        await store.FlushAllAsync(TestContext.Current.CancellationToken);

        inner.LastSaved.Should().BeSameAs(group);
    }

    [Fact]
    public async Task LoadAsync_ShouldFlushPendingBeforeReading()
    {
        var inner = new RecordingGroupStore();
        using var store = Create(inner, debounce: TimeSpan.FromSeconds(10));

        await store.SaveAsync(new SessionGroup { Id = "g1", Title = "A" }, TestContext.Current.CancellationToken);

        var loaded = await store.LoadAsync("g1", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        loaded!.Title.Should().Be("A");
        inner.IndexOfEvent("save:g1").Should().BeLessThan(inner.IndexOfEvent("load:g1"));
    }

    [Fact]
    public async Task LoadAsync_WhenInnerWriteKeepsFailing_ShouldNotHang()
    {
        var inner = new RecordingGroupStore
        {
            SaveInterceptor = (_, _) => Task.FromException(new IOException("持续失败"))
        };
        using var store = Create(
            inner,
            debounce: TimeSpan.FromMilliseconds(20),
            readFlush: TimeSpan.FromMilliseconds(80),
            shutdown: TimeSpan.FromMilliseconds(200));

        await store.SaveAsync(new SessionGroup { Id = "g1", Title = "A" }, TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();
        var loaded = await store.LoadAsync("g1", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        stopwatch.Stop();

        loaded.Should().BeNull();
        inner.SaveCount.Should().Be(0);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DeleteAsync_AfterPendingSave_ShouldDiscardAndNotResurrect()
    {
        var inner = new RecordingGroupStore();
        using var store = Create(inner, debounce: TimeSpan.FromSeconds(10));

        await store.SaveAsync(new SessionGroup { Id = "g1", Title = "A" }, TestContext.Current.CancellationToken);
        await store.DeleteAsync("g1", TestContext.Current.CancellationToken);

        await Task.Delay(150, TestContext.Current.CancellationToken);

        inner.DeleteCount.Should().Be(1);
        inner.SaveCount.Should().Be(0);
        (await inner.LoadAsync("g1", TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task FindBySessionAsync_ShouldFlushBeforeDelegate()
    {
        var inner = new RecordingGroupStore();
        using var store = Create(inner, debounce: TimeSpan.FromSeconds(10));

        await store.SaveAsync(new SessionGroup
        {
            Id = "g1",
            Title = "A",
            Members = [new SessionGroupMember { SessionId = "s1" }]
        }, TestContext.Current.CancellationToken);

        var found = await store.FindBySessionAsync("s1", TestContext.Current.CancellationToken);

        found!.Id.Should().Be("g1");
        inner.IndexOfEvent("save:g1").Should().BeLessThan(inner.IndexOfEvent("find:s1"));
    }

    [Fact]
    public async Task ListAsync_ShouldFlushBeforeDelegate()
    {
        var inner = new RecordingGroupStore();
        using var store = Create(inner, debounce: TimeSpan.FromSeconds(10));

        await store.SaveAsync(new SessionGroup { Id = "g1", Title = "A" }, TestContext.Current.CancellationToken);

        var list = await store.ListAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        list.Should().ContainSingle();
        inner.IndexOfEvent("save:g1").Should().BeLessThan(inner.IndexOfEvent("list"));
    }

    [Fact]
    public async Task FlushAsync_ShouldWaitForWriteCompletion()
    {
        var inner = new RecordingGroupStore();
        using var store = Create(inner, debounce: TimeSpan.FromSeconds(10));

        await store.SaveAsync(new SessionGroup { Id = "g1", Title = "A" }, TestContext.Current.CancellationToken);
        await store.FlushAsync("g1", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        inner.SaveCount.Should().Be(1);
        inner.LastSaved!.Title.Should().Be("A");
    }

    [Fact]
    public async Task Relocatable_ShouldForwardBaseDirectoryAndSetBaseDirectory()
    {
        var inner = new RelocatableRecordingGroupStore { BaseDirectory = "C:\\ws1" };
        using var store = Create(inner);

        store.BaseDirectory.Should().Be("C:\\ws1");

        store.SetBaseDirectory("C:\\ws2");

        inner.BaseDirectory.Should().Be("C:\\ws2");
        store.BaseDirectory.Should().Be("C:\\ws2");
    }

    [Fact]
    public void Relocatable_WhenInnerNotRelocatable_ShouldBeEmptyAndNoOp()
    {
        var inner = new RecordingGroupStore();
        using var store = Create(inner);

        store.BaseDirectory.Should().BeEmpty();

        var act = () => store.SetBaseDirectory("C:\\ws2");
        act.Should().NotThrow();
    }

    [Fact]
    public async Task DisposeAsync_ShouldFlushPendingWrites()
    {
        var inner = new RecordingGroupStore();
        var store = Create(inner, debounce: TimeSpan.FromSeconds(10));

        await store.SaveAsync(new SessionGroup { Id = "g1", Title = "A" }, TestContext.Current.CancellationToken);
        await store.DisposeAsync();

        inner.SaveCount.Should().Be(1);
        inner.LastSaved!.Title.Should().Be("A");
    }

    [Fact]
    public async Task Dispose_ShouldFlushPendingWrites()
    {
        var inner = new RecordingGroupStore();
        var store = Create(inner, debounce: TimeSpan.FromSeconds(10));

        await store.SaveAsync(new SessionGroup { Id = "g1", Title = "A" }, TestContext.Current.CancellationToken);
        store.Dispose();

        inner.SaveCount.Should().Be(1);
    }

    private static WriteBehindSessionGroupStore Create(
        ISessionGroupStore inner,
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

    /// <summary>记录调用次数、顺序与最后写入对象的内存会话组存储实现。</summary>
    private class RecordingGroupStore : ISessionGroupStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, SessionGroup> _saved = new(StringComparer.Ordinal);
        private readonly List<string> _events = new();

        /// <summary>可选保存拦截器（抛异常以模拟持续写失败）。</summary>
        public Func<SessionGroup, CancellationToken, Task>? SaveInterceptor { get; set; }

        public int SaveCount { get; private set; }

        public int LoadCount { get; private set; }

        public int DeleteCount { get; private set; }

        public int ListCount { get; private set; }

        public int FindCount { get; private set; }

        public SessionGroup? LastSaved { get; private set; }

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

        public Task<SessionGroup?> LoadAsync(string groupId, CancellationToken ct = default)
        {
            lock (_gate)
            {
                LoadCount++;
                _events.Add("load:" + groupId);
                return Task.FromResult(_saved.TryGetValue(groupId, out var group) ? group : null);
            }
        }

        public Task<SessionGroup?> FindBySessionAsync(string sessionId, CancellationToken ct = default)
        {
            lock (_gate)
            {
                FindCount++;
                _events.Add("find:" + sessionId);
                var match = _saved.Values.FirstOrDefault(g => g.Members.Any(m => m.SessionId == sessionId));
                return Task.FromResult(match);
            }
        }

        public async Task SaveAsync(SessionGroup group, CancellationToken ct = default)
        {
            if (SaveInterceptor is not null)
                await SaveInterceptor(group, ct).ConfigureAwait(false);

            lock (_gate)
            {
                SaveCount++;
                LastSaved = group;
                _saved[group.Id] = group;
                _events.Add("save:" + group.Id);
            }
        }

        public Task DeleteAsync(string groupId, CancellationToken ct = default)
        {
            lock (_gate)
            {
                DeleteCount++;
                _saved.Remove(groupId);
                _events.Add("delete:" + groupId);
            }

            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<SessionGroup> ListAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            List<SessionGroup> snapshot;
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
    }

    /// <summary>支持重定位能力的内层会话组存储。</summary>
    private sealed class RelocatableRecordingGroupStore : RecordingGroupStore, IRelocatableSessionGroupStore
    {
        public string BaseDirectory { get; set; } = "C:\\workspace-initial";

        public void SetBaseDirectory(string baseDirectory) => BaseDirectory = baseDirectory;
    }
}

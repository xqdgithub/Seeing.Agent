using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Background;
using Seeing.IO.Local;
using Xunit;

namespace Seeing.Agent.Tests.Background;

/// <summary>
/// <see cref="BackgroundActivityStore"/> 的快照读写容错、孤儿收敛与逐活动输出限额。
/// <para>
/// 断言全部走可观察事实（磁盘文件、返回快照、发布记录），不使用固定时长等待。
/// 宿主存活判定用真实进程事实构造：当前进程 = 存活，不存在的 pid = 已死。
/// </para>
/// </summary>
public class BackgroundActivityStoreTests : IDisposable
{
    private const string Kind = "process";

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    // ─── 快照落盘 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAndLoad_ShouldRoundTrip()
    {
        var store = CreateStore();
        var activityId = NewActivityId();
        var expected = Snapshot(activityId, LiveHostInstanceId(), BackgroundActivityStatus.Running, seq: 2);

        await store.SaveAsync(expected);

        var path = SnapshotPath(activityId);
        File.Exists(path).Should().BeTrue();

        // 可读 JSON：缩进 + 枚举按字符串（不是数字），便于人工排查与跨版本兼容。
        var json = File.ReadAllText(path);
        json.Should().Contain("\n").And.Contain("\"Status\": \"Running\"");

        var loaded = await store.LoadAsync(activityId);
        loaded.Should().NotBeNull();
        loaded!.Should().BeEquivalentTo(expected);
        loaded.Attributes.Should().ContainKey("pid");

        // 覆盖写：同一 id 再写一次，读到的是最新版本而不是两份。
        await store.SaveAsync(expected with { Status = BackgroundActivityStatus.Succeeded, Seq = 3 });
        (await store.LoadAsync(activityId))!.Status.Should().Be(BackgroundActivityStatus.Succeeded);
        (await store.LoadAllAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task CorruptedFile_ShouldBeSkipped()
    {
        var store = CreateStore();
        var corruptedId = NewActivityId();
        var validId = NewActivityId();

        Directory.CreateDirectory(ActivityRoot());
        await File.WriteAllTextAsync(SnapshotPath(corruptedId), "{ \"Id\": \"act_truncated");
        await File.WriteAllTextAsync(Path.Combine(ActivityRoot(), "notes.json"), "not an activity");
        await store.SaveAsync(Snapshot(validId, LiveHostInstanceId(), BackgroundActivityStatus.Running));

        (await store.LoadAsync(corruptedId)).Should().BeNull();

        var all = await store.LoadAllAsync();
        all.Should().ContainSingle();
        all[0].Id.Should().Be(validId);
    }

    // ─── 孤儿收敛 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reconcile_ShouldMarkDeadHostRunningAsInterrupted()
    {
        var store = CreateStore();
        var publisher = new FakePublisher();
        var manager = CreateManager(store, publisher);

        var deadPid = $"{FindFreePid()}:0";
        var deadStartTicks = LivePidWithDifferentStartTicks();

        var deadId = NewActivityId();
        var reusedPidId = NewActivityId();
        var terminalId = NewActivityId();

        await store.SaveAsync(Snapshot(deadId, deadPid, BackgroundActivityStatus.Running, seq: 3));
        await store.SaveAsync(Snapshot(reusedPidId, deadStartTicks, BackgroundActivityStatus.Running, seq: 1));
        // 终态记录是权威事实：即使宿主已死也不得改写。
        await store.SaveAsync(Snapshot(terminalId, deadPid, BackgroundActivityStatus.Succeeded, seq: 5));

        var count = await manager.ReconcileOrphanedActivitiesAsync();

        count.Should().Be(2);

        var dead = await store.LoadAsync(deadId);
        dead!.Status.Should().Be(BackgroundActivityStatus.Interrupted);
        dead.CancelReason.Should().Be(BackgroundActivityCancelReason.HostShutdown);
        dead.CompletedAt.Should().NotBeNull();
        dead.Seq.Should().Be(4);

        // 同 pid 但启动时间不同 → pid 已被复用，旧宿主已死。
        (await store.LoadAsync(reusedPidId))!.Status.Should().Be(BackgroundActivityStatus.Interrupted);

        var terminal = await store.LoadAsync(terminalId);
        terminal!.Status.Should().Be(BackgroundActivityStatus.Succeeded);
        terminal.Seq.Should().Be(5);
    }

    [Fact]
    public async Task Reconcile_ShouldNotTouchLiveHost()
    {
        var store = CreateStore();
        var publisher = new FakePublisher();
        var manager = CreateManager(store, publisher);

        var activityId = NewActivityId();
        await store.SaveAsync(Snapshot(activityId, LiveHostInstanceId(), BackgroundActivityStatus.Running, seq: 1));

        var count = await manager.ReconcileOrphanedActivitiesAsync();

        count.Should().Be(0);
        var loaded = await store.LoadAsync(activityId);
        loaded!.Status.Should().Be(BackgroundActivityStatus.Running);
        loaded.Seq.Should().Be(1);
        loaded.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task Reconcile_ShouldNotWriteOutboxOrPublish()
    {
        var store = CreateStore();
        var publisher = new FakePublisher();
        var manager = CreateManager(store, publisher);

        var activityId = NewActivityId();
        await store.SaveAsync(Snapshot(activityId, $"{FindFreePid()}:0", BackgroundActivityStatus.Running));

        (await manager.ReconcileOrphanedActivitiesAsync()).Should().Be(1);

        // 不发事件（事件是「已持久事实」的实时投影，孤儿收敛不是新事实，只是补记）。
        publisher.Events.Should().BeEmpty();

        // 不写 outbox：收敛只落记录，不产生待投递批次（spec §16）。
        Directory.Exists(Path.Combine(ActivityRoot(), "outbox")).Should().BeFalse();

        // 也不产生输出文件：孤儿收敛不碰输出。
        Directory.GetFiles(ActivityRoot())
            .Select(Path.GetFileName)
            .Should()
            .BeEquivalentTo(new[] { activityId + BackgroundActivityStore.FileSuffix });
    }

    // ─── 输出限额 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task OutputLimit_ShouldBeAppliedPerActivity()
    {
        const long defaultLimit = 1024 * 1024;
        var store = CreateOutputStore(defaultLimit);
        var smallId = NewActivityId();
        var largeId = NewActivityId();

        store.Configure(smallId, 100);
        store.Configure(largeId, 300);

        await store.AppendAsync(smallId, Fill('a', 400), CancellationToken.None);
        await store.AppendAsync(largeId, Fill('b', 100), CancellationToken.None);

        var smallStats = store.TryGetStats(smallId);
        smallStats.Should().NotBeNull();
        smallStats!.Value.TotalBytes.Should().Be(100);
        smallStats.Value.Truncated.Should().BeTrue();

        // 另一个活动不受影响：限额是逐活动的，不是进程级。
        var largeStats = store.TryGetStats(largeId);
        largeStats.Should().NotBeNull();
        largeStats!.Value.TotalBytes.Should().Be(100);
        largeStats.Value.Truncated.Should().BeFalse();

        // 用满自己的上限后同样截断（300 = 已写 100 + 本次可写 200）。
        await store.AppendAsync(largeId, Fill('c', 400), CancellationToken.None);
        var afterLimit = store.TryGetStats(largeId);
        afterLimit!.Value.TotalBytes.Should().Be(300);
        afterLimit.Value.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task StartAsync_ShouldConfigureOutputLimitPerActivity()
    {
        var store = CreateStore();
        var publisher = new FakePublisher();
        var output = new FakeOutputStore();
        var options = new BackgroundActivityOptions { DefaultOutputLimitBytes = 4096 };
        var manager = CreateManager(store, publisher, output, options);
        var launcher = new NoopLauncher();
        manager.RegisterLauncher("test-module", launcher);

        var withRequestLimit = await manager.StartAsync(
            Request("s1") with { OutputLimitBytes = 123 },
            "s1");
        var withDefaultLimit = await manager.StartAsync(Request("s1"), "s1");

        output.Limits[withRequestLimit.ActivityId].Should().Be(123);
        output.Limits[withDefaultLimit.ActivityId].Should().Be(options.DefaultOutputLimitBytes);
    }

    // ─── 工具 ─────────────────────────────────────────────────────────────────

    private IWorkspaceProvider CreateWorkspace()
        => Mock.Of<IWorkspaceProvider>(
            w => w.ProjectSeeingDirectory == Path.Combine(_tempRoot, ".seeing"));

    private BackgroundActivityStore CreateStore()
        => new(CreateWorkspace(), NullLogger<BackgroundActivityStore>.Instance);

    private BackgroundActivityOutputStore CreateOutputStore(long defaultLimitBytes)
        => new(
            new ProcessPathWriteLock(),
            CreateWorkspace(),
            new FixedOptionsMonitor<BackgroundActivityOptions>(
                new BackgroundActivityOptions { DefaultOutputLimitBytes = defaultLimitBytes }),
            NullLogger<BackgroundActivityOutputStore>.Instance);

    private static BackgroundActivityManager CreateManager(
        IBackgroundActivityStore store,
        FakePublisher publisher,
        FakeOutputStore? output = null,
        BackgroundActivityOptions? options = null)
        => new(
            publisher,
            new ServiceCollection().BuildServiceProvider(),
            new FixedOptionsMonitor<BackgroundActivityOptions>(options ?? new BackgroundActivityOptions()),
            TimeProvider.System,
            store,
            output ?? new FakeOutputStore(),
            new NoopNotificationQueue(),
            NullLogger<BackgroundActivityManager>.Instance);

    private static BackgroundActivitySnapshot Snapshot(
        string activityId,
        string hostInstanceId,
        BackgroundActivityStatus status,
        long seq = 0)
        => new()
        {
            Id = activityId,
            Kind = Kind,
            Title = "构建",
            Origin = BackgroundActivityOrigin.ToolBackground,
            WorkspaceRoot = @"C:\ws",
            HostInstanceId = hostInstanceId,
            CreatedAt = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.FromHours(8)),
            OwnerSessionId = "s1",
            OwnerExecutionId = "exec-1",
            Status = status,
            Seq = seq,
            Capabilities = BackgroundActivityCapabilities.CanCancel
                | BackgroundActivityCapabilities.CanStreamOutput,
            NotifyPolicy = BackgroundActivityNotifyPolicy.OnComplete,
            Timeout = TimeSpan.FromMinutes(5),
            OutputLimitBytes = 1024,
            Attributes = new Dictionary<string, string>(StringComparer.Ordinal) { ["pid"] = "123" }
        };

    private static BackgroundActivityStartRequest Request(string ownerSessionId)
        => new()
        {
            Kind = Kind,
            Title = "构建",
            OwnerSessionId = ownerSessionId,
            OwnerExecutionId = "exec-1"
        };

    /// <summary>当前进程的宿主标识（与 manager 的构造口径一致）：真实存活。</summary>
    private static string LiveHostInstanceId()
    {
        using var process = Process.GetCurrentProcess();
        return $"{process.Id}:{process.StartTime.ToUniversalTime().Ticks}";
    }

    /// <summary>同 pid 但启动时间不同：模拟 pid 已被复用，旧宿主已死。</summary>
    private static string LivePidWithDifferentStartTicks()
    {
        using var process = Process.GetCurrentProcess();
        return $"{process.Id}:{process.StartTime.ToUniversalTime().Ticks + TimeSpan.TicksPerHour}";
    }

    /// <summary>找一个当前不存在进程的 pid（用真实进程事实构造「已死宿主」）。</summary>
    private static int FindFreePid()
    {
        for (var pid = 90000; pid > 4000; pid--)
        {
            if (!PidExists(pid))
                return pid;
        }

        throw new InvalidOperationException("找不到空闲 pid，无法构造死宿主场景。");
    }

    private static bool PidExists(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            // 访问受限：进程存在但不可读。
            return true;
        }
    }

    private string ActivityRoot() => Path.Combine(_tempRoot, ".seeing", BackgroundActivityStore.DirectoryName);

    private string SnapshotPath(string activityId) => Path.Combine(ActivityRoot(), activityId + BackgroundActivityStore.FileSuffix);

    private static string NewActivityId() => "act_" + Guid.NewGuid().ToString("N");

    private static byte[] Fill(char value, int length) => Encoding.ASCII.GetBytes(new string(value, length));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch (Exception)
        {
            // 清理失败不影响断言结果。
        }
    }

    // ─── 测试替身 ─────────────────────────────────────────────────────────────

    private sealed class FixedOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public FixedOptionsMonitor(T value) => CurrentValue = value;

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class NoopNotificationQueue : IBackgroundActivityNotificationQueue
    {
        public Task EnqueueAsync(BackgroundActivityNotification notification) => Task.CompletedTask;

        public Task<int> MarkDroppedAsync(string hostInstanceId, string? projectRoot = null, string? reason = null)
            => Task.FromResult(0);
    }

    private sealed class FakeOutputStore : IActivityOutputStore
    {        public ConcurrentDictionary<string, long> Limits { get; } = new(StringComparer.Ordinal);

        public void Configure(string activityId, long limitBytes) => Limits[activityId] = limitBytes;

        public Task<ActivityOutputAppendResult> AppendAsync(
            string activityId,
            ReadOnlyMemory<byte> chunk,
            CancellationToken ct)
            => Task.FromResult(new ActivityOutputAppendResult(0, Truncated: false));

        public Task<BackgroundActivityOutputPage> ReadAsync(
            string activityId,
            long offset,
            int maxBytes,
            CancellationToken ct)
            => Task.FromResult(new BackgroundActivityOutputPage(string.Empty, offset, EndOfStream: true, Truncated: false));

        public Task DeleteAsync(string activityId) => Task.CompletedTask;
    }

    private sealed class FakePublisher : IExecutionEventPublisher
    {
        private readonly List<IMessageEvent> _events = new();
        private readonly object _gate = new();

        public IReadOnlyList<IMessageEvent> Events
        {
            get
            {
                lock (_gate)
                    return _events.ToList();
            }
        }

        public void Publish(string sessionId, IMessageEvent evt)
        {
            lock (_gate)
                _events.Add(evt);
        }

        public IAsyncEnumerable<IMessageEvent> SubscribeAsync(string sessionId, CancellationToken cancellationToken)
            => EmptyEventsAsync();

        public ILiveEventSubscription SubscribeLive(string sessionId) => throw new NotSupportedException();

        public IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId) => Array.Empty<IMessageEvent>();

        public void ClearBuffer(string sessionId)
        {
        }

        public void CompleteSession(string sessionId)
        {
        }

        private static async IAsyncEnumerable<IMessageEvent> EmptyEventsAsync()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class NoopLauncher : IBackgroundActivityLauncher
    {
        /// <summary>永不完成的执行：活动保持 Running，不触发终态。</summary>
        private readonly TaskCompletionSource _execution = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Kind => "process";

        public BackgroundActivityCapabilities Capabilities => BackgroundActivityCapabilities.CanCancel;

        public Task<BackgroundActivityLaunchResult> LaunchAsync(
            BackgroundActivityLaunchContext context,
            CancellationToken ct)
            => Task.FromResult(new BackgroundActivityLaunchResult(_execution.Task));

        public Task CancelAsync(BackgroundActivityHandle handle, BackgroundActivityCancelReason reason)
            => Task.CompletedTask;
    }
}

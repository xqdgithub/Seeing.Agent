using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Background;
using Xunit;

namespace Seeing.Agent.Tests.Background;

/// <summary>
/// outbox 的合并、幂等、已观察过滤、多实例隔离与重试上限，以及 manager 的入队接线。
/// <para>
/// 合并窗口全部由 <see cref="FakeTimeProvider"/> 驱动（推进时间后单步泵取），
/// 不使用固定时长等待。
/// </para>
/// </summary>
public class BackgroundActivityOutboxTests : IDisposable
{
    private const string Kind = "process";

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    // ─── 合并与幂等 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task PumpOnce_ShouldMergeFiveCompletionsIntoOneMessage()
    {
        var harness = CreateHarness();

        for (var i = 1; i <= 5; i++)
        {
            var activityId = NewActivityId();
            harness.Store.Add(Snapshot(activityId, "s1", title: $"构建 {i}"));
            await harness.Outbox.EnqueueAsync(Notification("s1", activityId));
        }

        harness.Time.Advance(Window);

        (await harness.Outbox.PumpOnceAsync()).Should().Be(1);

        var message = harness.Notifier.Messages.Should().ContainSingle().Subject;
        message.TargetSessionId.Should().Be("s1");
        message.Delivery.Should().Be(InboxDelivery.Wake);
        message.Presentation.Should().Be(InboxPresentation.System);
        message.SourceTag.Should().Be(BackgroundActivityOutbox.SourceTag);
        message.Sender.Kind.Should().Be(InboxSenderKind.System);
        message.Sender.Id.Should().Be("background");
        message.IdempotencyKey.Should().MatchRegex("^activity-digest:dgt_[0-9a-f]{32}$");

        // 五条活动在正文里各占一行（摘要，不含输出正文）。
        message.Body!.Split('\n').Count(line => line.StartsWith("- ", StringComparison.Ordinal))
            .Should().Be(5);
        message.Body.Should().Contain("构建 1").And.Contain("构建 5").And.Contain($"kind={Kind}");

        harness.Outbox.PendingCount.Should().Be(0);

        // 盘上：1 条 digest（Delivered）+ 5 条源记录（已并入 digest）。
        var files = Directory.GetFiles(OutboxDirectory());
        files.Should().HaveCount(6);
        files.Select(File.ReadAllText).Should().NotContain(text => text.Contains("\"Status\": \"Pending\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PumpOnce_Twice_ShouldNotRedeliver()
    {
        var harness = CreateHarness();
        var activityId = NewActivityId();
        harness.Store.Add(Snapshot(activityId, "s1"));
        await harness.Outbox.EnqueueAsync(Notification("s1", activityId));

        harness.Time.Advance(Window);
        (await harness.Outbox.PumpOnceAsync()).Should().Be(1);

        harness.Time.Advance(Window);
        (await harness.Outbox.PumpOnceAsync()).Should().Be(0);

        harness.Notifier.Messages.Should().ContainSingle();
        harness.Outbox.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task PumpOnce_ShouldSkipObservedResults()
    {
        var harness = CreateHarness();
        var observedId = NewActivityId();
        var pendingId = NewActivityId();

        harness.Store.Add(Snapshot(observedId, "s1", title: "已看过", resultObservedAt: DateTimeOffset.Now));
        harness.Store.Add(Snapshot(pendingId, "s1", title: "未看过"));

        await harness.Outbox.EnqueueAsync(Notification("s1", observedId));
        await harness.Outbox.EnqueueAsync(Notification("s1", pendingId));

        harness.Time.Advance(Window);
        (await harness.Outbox.PumpOnceAsync()).Should().Be(1);

        var message = harness.Notifier.Messages.Should().ContainSingle().Subject;
        message.Body.Should().Contain("未看过").And.NotContain("已看过");
        ReadNotifications().Should().Contain(n => n.Status == BackgroundActivityOutboxStatus.Delivered
                                                  && n.LastError == "merged_into:" + DigestOf(message));

        // 全部已观察：不投递，直接标 delivered（spec §10.6）。
        var allObservedId = NewActivityId();
        harness.Store.Add(Snapshot(allObservedId, "s1", title: "也看过", resultObservedAt: DateTimeOffset.Now));
        await harness.Outbox.EnqueueAsync(Notification("s1", allObservedId));

        harness.Time.Advance(Window);
        (await harness.Outbox.PumpOnceAsync()).Should().Be(0);

        harness.Notifier.Messages.Should().ContainSingle();
        ReadNotifications().Should().Contain(n => n.Status == BackgroundActivityOutboxStatus.Delivered
                                                  && n.LastError == "result_observed");
    }

    [Fact]
    public async Task PumpOnce_UnreadableActivity_ShouldRetryThenDrop()
    {
        var harness = CreateHarness();
        var activityId = NewActivityId();
        harness.Store.Add(Snapshot(activityId, "s1"));
        harness.Store.Unreadable.Add(activityId);

        await harness.Outbox.EnqueueAsync(Notification("s1", activityId));

        // 首轮读不出：不投递、不标终态，只计一次失败。
        harness.Time.Advance(Window);
        (await harness.Outbox.PumpOnceAsync()).Should().Be(0);

        harness.Notifier.Messages.Should().BeEmpty();
        var first = ReadNotifications().Should().ContainSingle().Subject;
        first.Status.Should().Be(BackgroundActivityOutboxStatus.Pending);
        first.Attempts.Should().Be(1);
        first.LastError.Should().Be("activity_unreadable");

        // 连续 5 轮仍读不出才丢弃。
        for (var attempt = 2; attempt <= BackgroundActivityOutbox.MaxDeliveryAttempts; attempt++)
        {
            harness.Time.Advance(Window);
            await harness.Outbox.PumpOnceAsync();
        }

        var dropped = ReadNotifications().Should().ContainSingle().Subject;
        dropped.Status.Should().Be(BackgroundActivityOutboxStatus.Dropped);
        dropped.Attempts.Should().Be(BackgroundActivityOutbox.MaxDeliveryAttempts);
        dropped.LastError.Should().Be("activity_unreadable");
        harness.Notifier.Messages.Should().BeEmpty();
        harness.Outbox.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task ReconcileDeadHosts_ShouldDropDeadHostPending()
    {
        var harness = CreateHarness();

        var deadHost = $"{FindFreePid()}:0";
        var deadOutbox = CreateOutbox(harness.Notifier, harness.Time, harness.Store, hostInstanceId: deadHost);

        var deadId = NewActivityId();
        var liveId = NewActivityId();
        harness.Store.Add(Snapshot(deadId, "s1"));
        harness.Store.Add(Snapshot(liveId, "s2"));

        await deadOutbox.EnqueueAsync(Notification("s1", deadId));
        await harness.Outbox.EnqueueAsync(Notification("s2", liveId));

        var dropped = await harness.Outbox.ReconcileDeadHostsAsync();

        dropped.Should().Be(1);

        var dead = ReadNotifications().Single(n => n.HostInstanceId == deadHost);
        dead.Status.Should().Be(BackgroundActivityOutboxStatus.Dropped);
        dead.LastError.Should().Be("host_shutdown");

        // 本实例的 pending 不受影响。
        ReadNotifications().Single(n => n.HostInstanceId == BackgroundActivityHostIdentity.Current)
            .Status.Should().Be(BackgroundActivityOutboxStatus.Pending);

        // 已丢弃的批次不会再被泵取。
        harness.Time.Advance(Window);
        await harness.Outbox.PumpOnceAsync();
        harness.Notifier.Messages.Should().ContainSingle()
            .Which.TargetSessionId.Should().Be("s2");
    }

    [Fact]
    public async Task ReconcileDeadHosts_ShouldNotTouchLiveHost()
    {
        var harness = CreateHarness();

        // 本进程 pid + 无启动时间（pid-only 退化记录）：与 Current 字符串不同，必须走进程探测分支。
        var liveHost = $"{Environment.ProcessId}:0";
        var liveOutbox = CreateOutbox(harness.Notifier, harness.Time, harness.Store, hostInstanceId: liveHost);

        var activityId = NewActivityId();
        harness.Store.Add(Snapshot(activityId, "s1"));
        await liveOutbox.EnqueueAsync(Notification("s1", activityId));

        (await harness.Outbox.ReconcileDeadHostsAsync()).Should().Be(0);

        var live = ReadNotifications().Should().ContainSingle().Subject;
        live.Status.Should().Be(BackgroundActivityOutboxStatus.Pending);
        live.HostInstanceId.Should().Be(liveHost);
    }

    [Fact]
    public async Task PumpOnce_ShouldOnlyTakeOwnHostPending()
    {
        var harness = CreateHarness();

        var mine = NewActivityId();
        harness.Store.Add(Snapshot(mine, "s1"));
        await harness.Outbox.EnqueueAsync(Notification("s1", mine));

        // 同一活动根、另一个宿主实例写入的 pending：本实例不得泵取。
        var otherHost = CreateOutbox(harness.Notifier, harness.Time, harness.Store, hostInstanceId: "999999:0");
        var foreign = NewActivityId();
        harness.Store.Add(Snapshot(foreign, "s2"));
        await otherHost.EnqueueAsync(Notification("s2", foreign));

        harness.Time.Advance(Window);
        (await harness.Outbox.PumpOnceAsync()).Should().Be(1);

        harness.Notifier.Messages.Should().ContainSingle()
            .Which.TargetSessionId.Should().Be("s1");

        var foreignFiles = Directory.GetFiles(OutboxDirectory())
            .Where(path => File.ReadAllText(path).Contains("\"HostInstanceId\": \"999999:0\"", StringComparison.Ordinal))
            .ToList();
        foreignFiles.Should().ContainSingle();
        File.ReadAllText(foreignFiles[0]).Should().Contain("\"Status\": \"Pending\"");
    }

    [Fact]
    public async Task MarkDropped_ShouldDropOwnPendingOfTheGivenRoot()
    {
        var harness = CreateHarness();

        var first = NewActivityId();
        var second = NewActivityId();
        harness.Store.Add(Snapshot(first, "s1"));
        harness.Store.Add(Snapshot(second, "s1"));
        await harness.Outbox.EnqueueAsync(Notification("s1", first));
        await harness.Outbox.EnqueueAsync(Notification("s1", second));

        var dropped = await harness.Outbox.MarkDroppedAsync(
            BackgroundActivityHostIdentity.Current,
            _tempRoot,
            reason: "workspace_switched");

        dropped.Should().Be(2);
        ReadNotifications().Should().OnlyContain(n =>
            n.Status == BackgroundActivityOutboxStatus.Dropped && n.LastError == "workspace_switched");
        harness.Outbox.PendingCount.Should().Be(0);

        // 已丢弃的批次不会再被泵取（切回旧根也不会补投）。
        harness.Time.Advance(Window);
        (await harness.Outbox.PumpOnceAsync()).Should().Be(0);
        harness.Notifier.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task PumpOnce_FailingNotifier_ShouldRetryThenDrop()
    {
        var harness = CreateHarness();
        harness.Notifier.OnDeliver = _ => throw new IOException("disk full");

        var activityId = NewActivityId();
        harness.Store.Add(Snapshot(activityId, "s1"));
        await harness.Outbox.EnqueueAsync(Notification("s1", activityId));

        for (var attempt = 1; attempt <= BackgroundActivityOutbox.MaxDeliveryAttempts; attempt++)
        {
            harness.Time.Advance(Window);
            await harness.Outbox.PumpOnceAsync();
        }

        harness.Notifier.Calls.Should().Be(BackgroundActivityOutbox.MaxDeliveryAttempts);
        harness.Outbox.PendingCount.Should().Be(0);

        var digest = ReadNotifications()
            .Where(n => n.ActivityIds.Contains(activityId))
            .OrderByDescending(n => n.CreatedAt)
            .First();
        digest.Status.Should().Be(BackgroundActivityOutboxStatus.Dropped);
        digest.Attempts.Should().Be(BackgroundActivityOutbox.MaxDeliveryAttempts);
        digest.LastError.Should().Be("disk full");
    }

    [Fact]
    public async Task Body_ShouldNotContainFullOutput()
    {
        var harness = CreateHarness();
        var activityId = NewActivityId();
        harness.Store.Add(Snapshot(activityId, "s1", title: "构建"));

        // 输出正文里放可识别的哨兵文本：通知只允许出现大小，不得出现正文。
        const string secret = "SUPER_SECRET_OUTPUT_BODY";
        Directory.CreateDirectory(ActivityRoot());
        await File.WriteAllTextAsync(
            Path.Combine(ActivityRoot(), activityId + BackgroundActivityPaths.OutputFileSuffix),
            secret + new string('x', 2048));

        await harness.Outbox.EnqueueAsync(Notification("s1", activityId));
        harness.Time.Advance(Window);
        await harness.Outbox.PumpOnceAsync();

        var message = harness.Notifier.Messages.Should().ContainSingle().Subject;
        message.Body.Should().NotContain(secret);
        message.Body.Should().NotContain("xxxx");
        message.Body.Should().Contain("输出 2 KB");
    }

    // ─── manager 接线 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Complete_WithSignalsCompletion_ShouldEnqueueNotification()
    {
        var harness = CreateManagerHarness(BackgroundActivityCapabilities.SignalsCompletion);
        var handle = await harness.Manager.StartAsync(Request("s1"), "s1");

        await handle.Reporter.CompleteAsync(new BackgroundActivityResult(
            BackgroundActivityResultKind.None, null, null, 0, null, null));

        var queued = harness.Queue.Enqueued.Should().ContainSingle().Subject;
        queued.OwnerSessionId.Should().Be("s1");
        queued.ActivityIds.Should().Equal(handle.ActivityId);
    }

    [Fact]
    public async Task Interrupted_OnHostShutdown_ShouldNotEnqueueNotification()
    {
        var harness = CreateManagerHarness(BackgroundActivityCapabilities.SignalsCompletion);
        await harness.Manager.StartAsync(Request("s1"), "s1");

        await harness.Manager.CancelAllAsync(BackgroundActivityCancelReason.HostShutdown);

        harness.Queue.Enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task Complete_WithoutSignalsCompletion_ShouldNotEnqueueNotification()
    {
        var harness = CreateManagerHarness(BackgroundActivityCapabilities.CanCancel);
        var handle = await harness.Manager.StartAsync(Request("s1"), "s1");

        await handle.Reporter.CompleteAsync(new BackgroundActivityResult(
            BackgroundActivityResultKind.None, null, null, 0, null, null));

        harness.Queue.Enqueued.Should().BeEmpty();
    }

    // ─── 工具 ─────────────────────────────────────────────────────────────────

    private static readonly TimeSpan Window = BackgroundActivityOutbox.DefaultMergeWindow;

    private OutboxHarness CreateHarness()
    {
        var store = new FakeActivityStore();
        var notifier = new RecordingNotifier();
        var time = new FakeTimeProvider();
        var outbox = CreateOutbox(notifier, time, store: store);
        return new OutboxHarness(outbox, store, notifier, time);
    }

    private BackgroundActivityOutbox CreateOutbox(
        RecordingNotifier notifier,
        FakeTimeProvider time,
        FakeActivityStore? store = null,
        string? hostInstanceId = null)
        => new(
            CreateWorkspace(),
            store ?? new FakeActivityStore(),
            notifier,
            time,
            NullLogger<BackgroundActivityOutbox>.Instance,
            mergeWindow: Window,
            hostInstanceId: hostInstanceId);

    private IWorkspaceProvider CreateWorkspace()
        => Mock.Of<IWorkspaceProvider>(
            w => w.ProjectSeeingDirectory == Path.Combine(_tempRoot, ".seeing"));

    private string ActivityRoot() => Path.Combine(_tempRoot, ".seeing", BackgroundActivityPaths.DirectoryName);

    private string OutboxDirectory() => Path.Combine(ActivityRoot(), BackgroundActivityOutbox.DirectoryName);

    private IReadOnlyList<BackgroundActivityNotification> ReadNotifications()
    {
        var directory = OutboxDirectory();
        if (!Directory.Exists(directory))
            return Array.Empty<BackgroundActivityNotification>();

        return Directory.GetFiles(directory, "*" + BackgroundActivityOutbox.FileSuffix)
            .Select(path => System.Text.Json.JsonSerializer.Deserialize<BackgroundActivityNotification>(
                File.ReadAllText(path),
                new System.Text.Json.JsonSerializerOptions
                {
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
                })!)
            .ToList();
    }

    private static string DigestOf(InboxMessage message)
        => message.IdempotencyKey![BackgroundActivityOutbox.IdempotencyKeyPrefix.Length..];

    private static BackgroundActivityNotification Notification(string sessionId, params string[] activityIds)
        => new()
        {
            DigestId = string.Empty,
            HostInstanceId = string.Empty,
            OwnerSessionId = sessionId,
            ActivityIds = activityIds
        };

    private static BackgroundActivitySnapshot Snapshot(
        string activityId,
        string ownerSessionId,
        string title = "构建",
        DateTimeOffset? resultObservedAt = null)
        => new()
        {
            Id = activityId,
            Kind = Kind,
            Title = title,
            Origin = BackgroundActivityOrigin.ToolBackground,
            WorkspaceRoot = @"C:\ws",
            HostInstanceId = BackgroundActivityHostIdentity.Current,
            CreatedAt = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.FromHours(8)),
            OwnerSessionId = ownerSessionId,
            OwnerExecutionId = "exec-1",
            Status = BackgroundActivityStatus.Succeeded,
            Seq = 3,
            CompletedAt = new DateTimeOffset(2026, 11, 20, 10, 1, 0, TimeSpan.FromHours(8)),
            ResultObservedAt = resultObservedAt,
            Capabilities = BackgroundActivityCapabilities.CanCancel | BackgroundActivityCapabilities.SignalsCompletion,
            NotifyPolicy = BackgroundActivityNotifyPolicy.OnComplete,
            OutputLimitBytes = 5 * 1024 * 1024,
            Result = new BackgroundActivityResult(BackgroundActivityResultKind.ProcessExit, null, null, 0, null, null)
        };

    private static string NewActivityId() => "act_" + Guid.NewGuid().ToString("N");

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
            using var process = System.Diagnostics.Process.GetProcessById(pid);
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
        catch (System.ComponentModel.Win32Exception)
        {
            // 访问受限：进程存在但不可读。
            return true;
        }
    }

    // ─── manager harness ──────────────────────────────────────────────────────

    private ManagerHarness CreateManagerHarness(BackgroundActivityCapabilities capabilities)
    {
        var queue = new RecordingQueue();
        var manager = new BackgroundActivityManager(
            new NullPublisher(),
            new ServiceCollection().BuildServiceProvider(),
            new FixedOptionsMonitor<BackgroundActivityOptions>(new BackgroundActivityOptions()),
            new FakeTimeProvider(),
            new FakeActivityStore(),
            new NullOutputStore(),
            queue,
            NullLogger<BackgroundActivityManager>.Instance);

        manager.RegisterLauncher("test-module", new FakeLauncher(capabilities));
        return new ManagerHarness(manager, queue);
    }

    private static BackgroundActivityStartRequest Request(string ownerSessionId)
        => new()
        {
            Kind = Kind,
            Title = "构建",
            OwnerSessionId = ownerSessionId,
            OwnerExecutionId = "exec-1"
        };

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

    private sealed record OutboxHarness(
        BackgroundActivityOutbox Outbox,
        FakeActivityStore Store,
        RecordingNotifier Notifier,
        FakeTimeProvider Time);

    private sealed record ManagerHarness(BackgroundActivityManager Manager, RecordingQueue Queue);

    private sealed class FakeActivityStore : IBackgroundActivityStore
    {
        private readonly ConcurrentDictionary<string, BackgroundActivitySnapshot> _snapshots
            = new(StringComparer.Ordinal);

        /// <summary>模拟「文件在但读不出」的活动 id（瞬时 IO/JSON 失败）。</summary>
        public HashSet<string> Unreadable { get; } = new(StringComparer.Ordinal);

        public void Add(BackgroundActivitySnapshot snapshot) => _snapshots[snapshot.Id] = snapshot;

        public Task SaveAsync(BackgroundActivitySnapshot snapshot)
        {
            _snapshots[snapshot.Id] = snapshot;
            return Task.CompletedTask;
        }

        public Task<BackgroundActivitySnapshot?> LoadAsync(string activityId)
            => Task.FromResult(_snapshots.TryGetValue(activityId, out var snapshot) ? snapshot : null);

        public Task<BackgroundActivitySnapshotBatch> LoadManyAsync(
            IReadOnlyCollection<string> activityIds)
        {
            var found = activityIds
                .Where(id => _snapshots.ContainsKey(id) && !Unreadable.Contains(id))
                .Select(id => _snapshots[id])
                .ToList();

            var unreadable = activityIds.Where(Unreadable.Contains).Distinct(StringComparer.Ordinal).ToList();

            return Task.FromResult(new BackgroundActivitySnapshotBatch(found, unreadable));
        }

        public Task<IReadOnlyList<BackgroundActivitySnapshot>> LoadAllAsync()
            => Task.FromResult<IReadOnlyList<BackgroundActivitySnapshot>>(_snapshots.Values.ToList());

        public Task<bool> ReplaceIfUnchangedAsync(
            string activityId,
            BackgroundActivityStatus expectedStatus,
            string expectedHostInstanceId,
            Func<BackgroundActivitySnapshot, BackgroundActivitySnapshot> replacement)
            => Task.FromResult(false);
    }

    private sealed class RecordingNotifier : IBackgroundActivityNotifier
    {
        private readonly List<InboxMessage> _messages = new();
        private readonly object _gate = new();

        public Func<InboxMessage, Task>? OnDeliver { get; set; }

        public int Calls { get; private set; }

        public IReadOnlyList<InboxMessage> Messages
        {
            get
            {
                lock (_gate)
                    return _messages.ToList();
            }
        }

        public async Task DeliverAsync(InboxMessage message, CancellationToken ct = default)
        {
            Calls++;

            if (OnDeliver is { } onDeliver)
                await onDeliver(message);

            lock (_gate)
                _messages.Add(message);
        }
    }

    private sealed class RecordingQueue : IBackgroundActivityNotificationQueue
    {
        private readonly List<BackgroundActivityNotification> _enqueued = new();
        private readonly object _gate = new();

        public IReadOnlyList<BackgroundActivityNotification> Enqueued
        {
            get
            {
                lock (_gate)
                    return _enqueued.ToList();
            }
        }

        public Task EnqueueAsync(BackgroundActivityNotification notification)
        {
            lock (_gate)
                _enqueued.Add(notification);

            return Task.CompletedTask;
        }

        public Task<int> MarkDroppedAsync(string hostInstanceId, string? projectRoot = null, string? reason = null)
            => Task.FromResult(0);
    }

    private sealed class FakeLauncher : IBackgroundActivityLauncher
    {
        private readonly TaskCompletionSource _execution = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeLauncher(BackgroundActivityCapabilities capabilities) => Capabilities = capabilities;

        public string Kind => "process";

        public BackgroundActivityCapabilities Capabilities { get; }

        public Task<BackgroundActivityLaunchResult> LaunchAsync(
            BackgroundActivityLaunchContext context,
            CancellationToken ct)
            => Task.FromResult(new BackgroundActivityLaunchResult(_execution.Task));

        public Task CancelAsync(BackgroundActivityHandle handle, BackgroundActivityCancelReason reason)
            => Task.CompletedTask;
    }

    private sealed class NullOutputStore : IActivityOutputStore
    {
        public void Configure(string activityId, long limitBytes)
        {
        }

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
            => Task.FromResult(new BackgroundActivityOutputPage(string.Empty, offset, true, false));

        public Task DeleteAsync(string activityId) => Task.CompletedTask;
    }

    private sealed class NullPublisher : IExecutionEventPublisher
    {
        public void Publish(string sessionId, IMessageEvent evt)
        {
        }

        public IAsyncEnumerable<IMessageEvent> SubscribeAsync(string sessionId, CancellationToken cancellationToken)
            => EmptyAsync();

        public ILiveEventSubscription SubscribeLive(string sessionId) => throw new NotSupportedException();

        public IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId) => Array.Empty<IMessageEvent>();

        public void ClearBuffer(string sessionId)
        {
        }

        public void CompleteSession(string sessionId)
        {
        }

        private static async IAsyncEnumerable<IMessageEvent> EmptyAsync()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FixedOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public FixedOptionsMonitor(T value) => CurrentValue = value;

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

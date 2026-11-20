using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Core.Background;
using Xunit;

namespace Seeing.Agent.Tests.Background;

/// <summary>
/// <see cref="BackgroundActivityManager"/> 的状态机、终态 CAS、归属校验与取消树。
/// <para>
/// 断言全部走可观察信号（<c>TerminalSignal</c> / <c>WaitForPersistenceAsync</c> / 终态快照），
/// 不使用固定时长等待。
/// </para>
/// </summary>
public class BackgroundActivityManagerTests
{
    private const string Kind = "fake";

    // ─── 启动与归属 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_ShouldReturnRunningActivity()
    {
        var harness = CreateHarness();

        var handle = await harness.Manager.StartAsync(Request("s1"), "s1");

        handle.ActivityId.Should().StartWith("act_");
        var snapshot = harness.Manager.Get(handle.ActivityId, "s1");
        snapshot.Should().NotBeNull();
        snapshot!.Status.Should().Be(BackgroundActivityStatus.Running);
        snapshot.Seq.Should().BeGreaterThan(0);
        snapshot.StartedAt.Should().NotBeNull();
        snapshot.OwnerSessionId.Should().Be("s1");
        snapshot.OwnerExecutionId.Should().Be("exec-1");
        snapshot.Capabilities.Should().Be(harness.Launcher.Capabilities);

        await harness.Manager.WaitForPersistenceAsync(handle.ActivityId);
        harness.Publisher.ActivityEvents.Should().ContainSingle(e => e.Type == ActivityEventTypes.Started);
        harness.Store.Saved.Should().Contain(s => s.Id == handle.ActivityId && s.Status == BackgroundActivityStatus.Running);
    }

    [Fact]
    public async Task StartAsync_WithForeignRequester_ShouldThrow()
    {
        var harness = CreateHarness();

        var act = () => harness.Manager.StartAsync(Request("owner"), "intruder");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        harness.Store.Saved.Should().BeEmpty();
        harness.Launcher.LaunchCount.Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_WithUnregisteredKind_ShouldThrow()
    {
        var harness = CreateHarness();

        var act = () => harness.Manager.StartAsync(Request("s1", kind: "nope"), "s1");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task StartAsync_WithForeignRequester_SystemFaceShouldSkipCheck()
    {
        var harness = CreateHarness();

        var handle = await harness.Manager.StartAsSystemAsync(Request("owner"));

        harness.Manager.Get(handle.ActivityId, "owner").Should().NotBeNull();
        harness.Manager.Get(handle.ActivityId, "other").Should().BeNull();
    }

    [Fact]
    public async Task StartAsync_WithDuplicateDedupeKey_ShouldReturnExistingActivity()
    {
        var harness = CreateHarness();
        var request = Request("s1") with { DedupeKey = "same-key" };

        var first = await harness.Manager.StartAsync(request, "s1");
        var second = await harness.Manager.StartAsync(request, "s1");

        second.ActivityId.Should().Be(first.ActivityId);
        harness.Launcher.LaunchCount.Should().Be(1);
    }

    // ─── 终态 CAS ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Complete_After_Cancel_ShouldBeIgnored()
    {
        var harness = CreateHarness();
        var handle = await harness.Manager.StartAsync(Request("s1"), "s1");
        await harness.Manager.WaitForPersistenceAsync(handle.ActivityId);

        (await harness.Manager.CancelAsync(handle.ActivityId, BackgroundActivityCancelReason.User, "s1"))
            .Should().BeTrue();

        var seqAfterCancel = harness.Manager.Get(handle.ActivityId, "s1")!.Seq;

        await handle.Reporter.CompleteAsync(new BackgroundActivityResult(
            BackgroundActivityResultKind.Text, null, null, null, null, null));
        await handle.Reporter.FailAsync("late", "迟到的失败");

        var snapshot = harness.Manager.Get(handle.ActivityId, "s1")!;
        snapshot.Status.Should().Be(BackgroundActivityStatus.Cancelled);
        snapshot.CancelReason.Should().Be(BackgroundActivityCancelReason.User);
        snapshot.Result.Should().BeNull();
        snapshot.Seq.Should().Be(seqAfterCancel);

        harness.Publisher.ActivityEvents
            .Should().NotContain(e => e.Type == ActivityEventTypes.Completed || e.Type == ActivityEventTypes.Failed);
        harness.Publisher.ActivityEvents
            .Should().ContainSingle(e => e.Type == ActivityEventTypes.Cancelled);
    }

    [Fact]
    public async Task Cancel_ShouldNotifyLauncherWithHandle()
    {
        var harness = CreateHarness();
        var handle = await harness.Manager.StartAsync(Request("s1"), "s1");

        await harness.Manager.CancelAsync(handle.ActivityId, BackgroundActivityCancelReason.User, "s1");

        harness.Launcher.CancelCalls.Should().ContainSingle()
            .Which.Should().Be((handle.ActivityId, BackgroundActivityCancelReason.User));
        handle.CancellationToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task TerminalState_ShouldPersistBeforePublish()
    {
        var journal = new List<string>();
        var harness = CreateHarness();
        harness.Store.OnSave = snapshot =>
        {
            lock (journal)
                journal.Add($"save:{snapshot.Status}");
            return Task.CompletedTask;
        };
        harness.Publisher.OnPublish = evt =>
        {
            if (evt is BackgroundActivityEvent activityEvent)
            {
                lock (journal)
                    journal.Add($"event:{activityEvent.Type}");
            }
        };

        var handle = await harness.Manager.StartAsync(Request("s1"), "s1");
        await harness.Manager.WaitForPersistenceAsync(handle.ActivityId);
        await harness.Manager.CancelAsync(handle.ActivityId, BackgroundActivityCancelReason.User, "s1");

        lock (journal)
        {
            var saveIndex = journal.IndexOf($"save:{BackgroundActivityStatus.Cancelled}");
            var eventIndex = journal.IndexOf($"event:{ActivityEventTypes.Cancelled}");
            saveIndex.Should().BeGreaterThanOrEqualTo(0);
            eventIndex.Should().BeGreaterThanOrEqualTo(0);
            saveIndex.Should().BeLessThan(eventIndex);
        }
    }

    [Fact]
    public async Task CompleteAsync_ShouldPersistBeforePublish()
    {
        var journal = new List<string>();
        var harness = CreateHarness();
        harness.Store.OnSave = snapshot =>
        {
            lock (journal)
                journal.Add($"save:{snapshot.Status}");
            return Task.CompletedTask;
        };
        harness.Publisher.OnPublish = evt =>
        {
            if (evt is BackgroundActivityEvent activityEvent)
            {
                lock (journal)
                    journal.Add($"event:{activityEvent.Type}");
            }
        };

        var handle = await harness.Manager.StartAsync(Request("s1"), "s1");
        await harness.Manager.WaitForPersistenceAsync(handle.ActivityId);
        lock (journal)
            journal.Clear();

        await handle.Reporter.CompleteAsync(new BackgroundActivityResult(
            BackgroundActivityResultKind.Text, null, null, 0, null, null));

        // CompleteAsync 返回时：终态已落盘，且事件在其后。
        lock (journal)
        {
            journal.Should().Equal(
                $"save:{BackgroundActivityStatus.Succeeded}",
                $"event:{ActivityEventTypes.Completed}");
        }

        harness.Store.Saved.Should().Contain(s =>
            s.Id == handle.ActivityId && s.Status == BackgroundActivityStatus.Succeeded);
    }

    [Fact]
    public async Task FailAsync_ShouldPersistTerminalResultBeforeReturning()
    {
        var harness = CreateHarness();
        var handle = await harness.Manager.StartAsync(Request("s1"), "s1");

        await handle.Reporter.FailAsync("boom", "失败原因");

        var persisted = harness.Store.Saved.LastOrDefault(s => s.Id == handle.ActivityId);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(BackgroundActivityStatus.Failed);
        persisted.Result!.ErrorCode.Should().Be("boom");
        persisted.Result.ErrorMessage.Should().Be("失败原因");
        harness.Publisher.ActivityEvents.Should().ContainSingle(e => e.Type == ActivityEventTypes.Failed);
    }

    [Fact]
    public async Task Execution_CompletingWithoutTerminalState_ShouldFail()
    {
        var harness = CreateHarness();
        var handle = await harness.Manager.StartAsync(Request("s1"), "s1");

        harness.Launcher.ExecutionSource.SetResult();
        await harness.Manager.WaitForPersistenceAsync(handle.ActivityId);
        await WaitUntilAsync(() => harness.Manager.Get(handle.ActivityId, "s1")!.Status == BackgroundActivityStatus.Failed);
        await harness.Manager.WaitForPersistenceAsync(handle.ActivityId);

        var snapshot = harness.Manager.Get(handle.ActivityId, "s1")!;
        snapshot.Status.Should().Be(BackgroundActivityStatus.Failed);
        snapshot.Result!.ErrorCode.Should().Be("no_terminal_state");
    }

    [Fact]
    public async Task Timeout_ShouldCancelWithTimeoutReason()
    {
        var time = new FakeTimeProvider();
        var harness = CreateHarness(timeProvider: time);
        var handle = await harness.Manager.StartAsync(Request("s1"), "s1");

        time.Advance(TimeSpan.FromMinutes(30));

        await WaitUntilAsync(() => harness.Manager.Get(handle.ActivityId, "s1")!.Status == BackgroundActivityStatus.Cancelled);
        await harness.Manager.WaitForPersistenceAsync(handle.ActivityId);

        var snapshot = harness.Manager.Get(handle.ActivityId, "s1")!;
        snapshot.CancelReason.Should().Be(BackgroundActivityCancelReason.Timeout);
    }

    // ─── 查询与归属 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task List_ShouldOnlyReturnOwnSession()
    {
        var harness = CreateHarness();
        var mine = await harness.Manager.StartAsync(Request("s1"), "s1");
        var foreign = await harness.Manager.StartAsSystemAsync(Request("s2"));

        var mineList = harness.Manager.List(new BackgroundActivityQuery(), "s1");
        mineList.Should().ContainSingle().Which.Id.Should().Be(mine.ActivityId);

        var foreignList = harness.Manager.List(new BackgroundActivityQuery(), "s2");
        foreignList.Should().ContainSingle().Which.Id.Should().Be(foreign.ActivityId);
    }

    [Fact]
    public async Task List_ShouldApplyStatusKindAndIncludeCompletedFilters()
    {
        var harness = CreateHarness();
        var running = await harness.Manager.StartAsync(Request("s1", title: "running"), "s1");
        var done = await harness.Manager.StartAsync(Request("s1", title: "done"), "s1");
        await done.Reporter.CompleteAsync(new BackgroundActivityResult(
            BackgroundActivityResultKind.None, null, null, null, null, null));

        harness.Manager.List(new BackgroundActivityQuery(IncludeCompleted: false), "s1")
            .Should().ContainSingle().Which.Id.Should().Be(running.ActivityId);

        harness.Manager.List(new BackgroundActivityQuery(Status: BackgroundActivityStatus.Succeeded), "s1")
            .Should().ContainSingle().Which.Id.Should().Be(done.ActivityId);

        harness.Manager.List(new BackgroundActivityQuery(Kind: "other"), "s1").Should().BeEmpty();
    }

    [Fact]
    public async Task Get_Wait_Cancel_ReadOutput_ShouldRespectOwnership()
    {
        var harness = CreateHarness();
        var handle = await harness.Manager.StartAsync(Request("s1"), "s1");

        // Visibility = Session：他人查询/等待/取消/读输出/标记观察一律不生效。
        harness.Manager.Get(handle.ActivityId, "s2").Should().BeNull();
        (await harness.Manager.CancelAsync(handle.ActivityId, BackgroundActivityCancelReason.User, "s2"))
            .Should().BeFalse();
        (await harness.Manager.WaitAsync(handle.ActivityId, TimeSpan.Zero, "s2")).Should()
            .Be(new BackgroundActivityWaitResult(BackgroundActivityWaitOutcome.TimedOut, null));
        (await harness.Manager.ReadOutputAsync(handle.ActivityId, 0, 1024, "s2")).Should()
            .Be(new BackgroundActivityOutputPage(string.Empty, 0, EndOfStream: true, Truncated: false));
        harness.Manager.MarkResultObserved(handle.ActivityId, "s2");

        var snapshot = harness.Manager.Get(handle.ActivityId, "s1")!;
        snapshot.Status.Should().Be(BackgroundActivityStatus.Running);
        snapshot.ResultObservedAt.Should().BeNull();
    }

    [Fact]
    public async Task Get_ProjectVisibility_ShouldAllowForeignSession()
    {
        var harness = CreateHarness();
        var handle = await harness.Manager.StartAsSystemAsync(
            Request("s1") with { Visibility = BackgroundActivityVisibility.Project });

        harness.Manager.Get(handle.ActivityId, "s2").Should().NotBeNull();
        harness.Manager.List(new BackgroundActivityQuery(), "s2").Should().BeEmpty();
    }

    [Fact]
    public async Task WaitAsync_ShouldReturnCompletedWhenTerminal()
    {
        var harness = CreateHarness();
        var handle = await harness.Manager.StartAsync(Request("s1"), "s1");
        await handle.Reporter.CompleteAsync(new BackgroundActivityResult(
            BackgroundActivityResultKind.None, null, null, null, null, null));

        var result = await harness.Manager.WaitAsync(handle.ActivityId, TimeSpan.FromSeconds(5), "s1");

        result.Outcome.Should().Be(BackgroundActivityWaitOutcome.Completed);
        result.Snapshot!.Status.Should().Be(BackgroundActivityStatus.Succeeded);
    }

    [Fact]
    public async Task WaitAsync_WithTerminalActivityAndZeroTimeout_ShouldReturnCompleted()
    {
        var harness = CreateHarness();
        var handle = await harness.Manager.StartAsync(Request("s1"), "s1");
        await handle.Reporter.FailAsync("boom", "失败");

        var result = await harness.Manager.WaitAsync(handle.ActivityId, TimeSpan.Zero, "s1");

        result.Outcome.Should().Be(BackgroundActivityWaitOutcome.Completed);
        result.Snapshot!.Result!.ErrorCode.Should().Be("boom");
    }

    [Fact]
    public async Task WaitAsync_WithCancelledCaller_ShouldReturnCallerCancelled()
    {
        var harness = CreateHarness();
        var handle = await harness.Manager.StartAsync(Request("s1"), "s1");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await harness.Manager.WaitAsync(handle.ActivityId, TimeSpan.FromMinutes(5), "s1", cts.Token);

        result.Outcome.Should().Be(BackgroundActivityWaitOutcome.CallerCancelled);
        result.Snapshot!.Status.Should().Be(BackgroundActivityStatus.Running);
    }

    [Fact]
    public async Task MarkResultObserved_ShouldStampAndBumpSeq()
    {
        var harness = CreateHarness();
        var handle = await harness.Manager.StartAsync(Request("s1"), "s1");
        var before = harness.Manager.Get(handle.ActivityId, "s1")!;

        harness.Manager.MarkResultObserved(handle.ActivityId, "s1");
        var after = harness.Manager.Get(handle.ActivityId, "s1")!;

        after.ResultObservedAt.Should().NotBeNull();
        after.Seq.Should().Be(before.Seq + 1);
    }

    // ─── 取消树与批量 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelAsync_ShouldCancelSubtree()
    {
        var harness = CreateHarness();
        var parent = await harness.Manager.StartAsync(Request("s1", title: "parent"), "s1");
        var child = await harness.Manager.StartAsync(
            Request("s1", title: "child") with { ParentActivityId = parent.ActivityId },
            "s1");

        (await harness.Manager.CancelAsync(parent.ActivityId, BackgroundActivityCancelReason.User, "s1"))
            .Should().BeTrue();
        await harness.Manager.WaitForPersistenceAsync(parent.ActivityId);
        await harness.Manager.WaitForPersistenceAsync(child.ActivityId);

        harness.Manager.Get(parent.ActivityId, "s1")!.Status.Should().Be(BackgroundActivityStatus.Cancelled);
        harness.Manager.Get(parent.ActivityId, "s1")!.CancelReason
            .Should().Be(BackgroundActivityCancelReason.User);
        harness.Manager.Get(child.ActivityId, "s1")!.Status.Should().Be(BackgroundActivityStatus.Cancelled);
        harness.Manager.Get(child.ActivityId, "s1")!.CancelReason
            .Should().Be(BackgroundActivityCancelReason.CascadeFromParent);
    }

    [Fact]
    public async Task CancelBySessionAndExecution_ShouldOnlyTouchMatching()
    {
        var harness = CreateHarness();
        var sessionA = await harness.Manager.StartAsync(Request("s1") with { OwnerExecutionId = "exec-a" }, "s1");
        var sessionB = await harness.Manager.StartAsync(Request("s1") with { OwnerExecutionId = "exec-b" }, "s1");
        var foreign = await harness.Manager.StartAsSystemAsync(Request("s2") with { OwnerExecutionId = "exec-a" });

        (await harness.Manager.CancelByExecutionAsync("exec-a", BackgroundActivityCancelReason.SessionStopped))
            .Should().Be(2);
        harness.Manager.Get(sessionA.ActivityId, "s1")!.Status.Should().Be(BackgroundActivityStatus.Cancelled);
        harness.Manager.Get(foreign.ActivityId, "s2")!.Status.Should().Be(BackgroundActivityStatus.Cancelled);
        harness.Manager.Get(sessionB.ActivityId, "s1")!.Status.Should().Be(BackgroundActivityStatus.Running);

        (await harness.Manager.CancelBySessionAsync("s1", BackgroundActivityCancelReason.SessionStopped))
            .Should().Be(1);
        harness.Manager.Get(sessionB.ActivityId, "s1")!.Status.Should().Be(BackgroundActivityStatus.Cancelled);
    }

    [Fact]
    public async Task CancelAllAsync_WithHostShutdown_ShouldMarkInterrupted()
    {
        var harness = CreateHarness();
        var handle = await harness.Manager.StartAsync(Request("s1"), "s1");

        (await harness.Manager.CancelAllAsync(BackgroundActivityCancelReason.HostShutdown)).Should().Be(1);

        var snapshot = harness.Manager.Get(handle.ActivityId, "s1")!;
        snapshot.Status.Should().Be(BackgroundActivityStatus.Interrupted);
        snapshot.CancelReason.Should().Be(BackgroundActivityCancelReason.HostShutdown);
    }

    // ─── launcher 登记 ────────────────────────────────────────────────────────

    [Fact]
    public void RegisterLauncher_DuplicateKind_ShouldThrow()
    {
        var harness = CreateHarness();

        var act = () => harness.Manager.RegisterLauncher("other-module", new FakeLauncher(Kind));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{Kind}*");
    }

    [Fact]
    public async Task UnregisterLauncherAsync_ShouldCancelRunningActivitiesOfKind()
    {
        var harness = CreateHarness();
        var first = await harness.Manager.StartAsync(Request("s1"), "s1");
        var second = await harness.Manager.StartAsync(Request("s1"), "s1");

        var cancelled = await harness.Manager.UnregisterLauncherAsync(Kind);

        cancelled.Should().Be(2);
        harness.Manager.Get(first.ActivityId, "s1")!.Status.Should().Be(BackgroundActivityStatus.Cancelled);
        harness.Manager.Get(second.ActivityId, "s1")!.Status.Should().Be(BackgroundActivityStatus.Cancelled);
        harness.Manager.Get(first.ActivityId, "s1")!.CancelReason
            .Should().Be(BackgroundActivityCancelReason.ModuleDeactivated);

        // 注销后同类启动立即失败。
        await FluentActions.Awaiting(() => harness.Manager.StartAsync(Request("s1"), "s1"))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task TrackAsync_WithCompletion_ShouldBecomeSucceededWithCapabilities()
    {
        var harness = CreateHarness();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handle = await harness.Manager.TrackAsync(new BackgroundActivityTrackRequest
        {
            Kind = "agent-session",
            Title = "子会话",
            OwnerSessionId = "s1",
            OwnerExecutionId = "exec-1",
            Capabilities = BackgroundActivityCapabilities.CanAttach,
            Completion = completion.Task
        });

        harness.Manager.Get(handle.ActivityId, "s1")!.Capabilities
            .Should().Be(BackgroundActivityCapabilities.CanAttach);

        completion.SetResult();
        await WaitUntilAsync(() => harness.Manager.Get(handle.ActivityId, "s1")!.Status == BackgroundActivityStatus.Succeeded);
    }

    // ─── 测试替身与工具 ───────────────────────────────────────────────────────

    private sealed class Harness
    {
        public required BackgroundActivityManager Manager { get; init; }
        public required FakeLauncher Launcher { get; init; }
        public required FakePublisher Publisher { get; init; }
        public required FakeStore Store { get; init; }
        public required FakeOutputStore Output { get; init; }
    }

    private static Harness CreateHarness(
        TimeProvider? timeProvider = null,
        BackgroundActivityOptions? options = null)
    {
        var launcher = new FakeLauncher(Kind);
        var publisher = new FakePublisher();
        var store = new FakeStore();
        var output = new FakeOutputStore();
        var effectiveOptions = options ?? new BackgroundActivityOptions();

        var services = new ServiceCollection().BuildServiceProvider();

        var manager = new BackgroundActivityManager(
            publisher,
            services,
            new FixedOptionsMonitor<BackgroundActivityOptions>(effectiveOptions),
            timeProvider ?? new FakeTimeProvider(),
            store,
            output,
            NullLogger<BackgroundActivityManager>.Instance);

        manager.RegisterLauncher("test-module", launcher);
        return new Harness { Manager = manager, Launcher = launcher, Publisher = publisher, Store = store, Output = output };
    }

    private static BackgroundActivityStartRequest Request(
        string ownerSessionId,
        string title = "后台活动",
        string kind = Kind)
        => new()
        {
            Kind = kind,
            Title = title,
            OwnerSessionId = ownerSessionId,
            OwnerExecutionId = "exec-1"
        };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("等待条件超时");

            await Task.Delay(10);
        }
    }

    private sealed class FixedOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public FixedOptionsMonitor(T value) => CurrentValue = value;

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FakeLauncher : IBackgroundActivityLauncher
    {
        public FakeLauncher(string kind) => Kind = kind;

        public TaskCompletionSource ExecutionSource { get; }
            = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Kind { get; }

        public BackgroundActivityCapabilities Capabilities { get; init; }
            = BackgroundActivityCapabilities.CanCancel | BackgroundActivityCapabilities.CanWait
              | BackgroundActivityCapabilities.CanStreamOutput;

        public int LaunchCount { get; private set; }

        public List<(string ActivityId, BackgroundActivityCancelReason Reason)> CancelCalls { get; } = new();

        public Task<BackgroundActivityLaunchResult> LaunchAsync(
            BackgroundActivityLaunchContext ctx,
            CancellationToken ct)
        {
            LaunchCount++;
            return Task.FromResult(new BackgroundActivityLaunchResult(ExecutionSource.Task));
        }

        public Task CancelAsync(BackgroundActivityHandle handle, BackgroundActivityCancelReason reason)
        {
            lock (CancelCalls)
                CancelCalls.Add((handle.ActivityId, reason));

            return Task.CompletedTask;
        }
    }

    private sealed class FakePublisher : IExecutionEventPublisher
    {
        private readonly List<IMessageEvent> _events = new();
        private readonly object _gate = new();

        public Action<IMessageEvent>? OnPublish { get; set; }

        public IReadOnlyList<BackgroundActivityEvent> ActivityEvents
        {
            get
            {
                lock (_gate)
                    return _events.OfType<BackgroundActivityEvent>().ToList();
            }
        }

        public void Publish(string sessionId, IMessageEvent evt)
        {
            lock (_gate)
                _events.Add(evt);

            OnPublish?.Invoke(evt);
        }

        public IAsyncEnumerable<IMessageEvent> SubscribeAsync(string sessionId, CancellationToken cancellationToken)
            => EmptyEventsAsync();

        private static async IAsyncEnumerable<IMessageEvent> EmptyEventsAsync()
        {
            await Task.CompletedTask;
            yield break;
        }

        public ILiveEventSubscription SubscribeLive(string sessionId) => throw new NotSupportedException();

        public IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId) => Array.Empty<IMessageEvent>();

        public void ClearBuffer(string sessionId)
        {
        }

        public void CompleteSession(string sessionId)
        {
        }
    }

    private sealed class FakeStore : IBackgroundActivityStore
    {
        private readonly List<BackgroundActivitySnapshot> _saved = new();
        private readonly object _gate = new();

        public Func<BackgroundActivitySnapshot, Task>? OnSave { get; set; }

        public IReadOnlyList<BackgroundActivitySnapshot> Saved
        {
            get
            {
                lock (_gate)
                    return _saved.ToList();
            }
        }

        public async Task SaveAsync(BackgroundActivitySnapshot snapshot)
        {
            lock (_gate)
                _saved.Add(snapshot);

            if (OnSave is { } onSave)
                await onSave(snapshot);
        }

        public Task<BackgroundActivitySnapshot?> LoadAsync(string activityId)
        {
            lock (_gate)
                return Task.FromResult(_saved.LastOrDefault(s => s.Id == activityId));
        }

        public Task<IReadOnlyList<BackgroundActivitySnapshot>> LoadAllAsync()
        {
            lock (_gate)
                return Task.FromResult<IReadOnlyList<BackgroundActivitySnapshot>>(_saved.ToList());
        }
    }

    private sealed class FakeOutputStore : IActivityOutputStore
    {
        public List<(string ActivityId, byte[] Chunk)> Appends { get; } = new();

        public Task AppendAsync(string activityId, ReadOnlyMemory<byte> chunk, CancellationToken ct)
        {
            lock (Appends)
                Appends.Add((activityId, chunk.ToArray()));

            return Task.CompletedTask;
        }

        public Task<BackgroundActivityOutputPage> ReadAsync(
            string activityId,
            long offset,
            int maxBytes,
            CancellationToken ct)
            => Task.FromResult(new BackgroundActivityOutputPage("out", offset + maxBytes, EndOfStream: false, Truncated: false));

        public Task DeleteAsync(string activityId) => Task.CompletedTask;
    }
}

using System.Text;
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

        // 去重命中必须可区分：调用方据此说明「命令没有重新执行」。
        first.IsExisting.Should().BeFalse("首次启动是新建活动");
        second.IsExisting.Should().BeTrue("命中在途活动只是复用，没有新建");
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

    [Fact]
    public async Task PersistOrder_ShouldFollowSeq_WhenRunningAndTerminalRace()
    {
        var harness = CreateHarness();
        harness.Store.BlockFirstSave();

        var handle = await harness.Manager.StartAsync(Request("s1"), "s1");

        // 确保 Running 已入链并卡在存储里（此时终态还未提交）。
        await harness.Store.FirstSaveStarted.WaitAsync(TimeSpan.FromSeconds(5));

        var cancelling = harness.Manager.CancelAsync(handle.ActivityId, BackgroundActivityCancelReason.User, "s1");
        harness.Store.ReleaseFirstSave();
        await cancelling;

        // 落盘顺序严格等于 Seq 顺序：最后落盘的一定是终态，Running 不会覆盖它。
        var statuses = harness.Store.Saved.Select(s => s.Status).ToList();
        statuses.Should().EndWith(new[] { BackgroundActivityStatus.Cancelled });
        statuses.Should().Contain(BackgroundActivityStatus.Running);

        var persisted = harness.Store.Saved.Last();
        persisted.Status.Should().Be(BackgroundActivityStatus.Cancelled);
        persisted.Seq.Should().Be(harness.Manager.Get(handle.ActivityId, "s1")!.Seq);
    }

    [Fact]
    public async Task CompleteAsync_WithFailedPersist_ShouldNotPublishTerminalEvent()
    {
        var harness = CreateHarness();
        harness.Store.OnSave = _ => throw new IOException("disk full");

        var handle = await harness.Manager.StartAsync(Request("s1"), "s1");
        await harness.Manager.WaitForPersistenceAsync(handle.ActivityId);

        // Running 落盘失败：连 Started 都不该发。
        harness.Publisher.ActivityEvents.Should().BeEmpty();

        await handle.Reporter.CompleteAsync(new BackgroundActivityResult(
            BackgroundActivityResultKind.Text, null, null, null, null, null));

        // 终态落盘失败：内存终态已提交，但事件不得发布（事件是权威记录的投影）。
        harness.Publisher.ActivityEvents.Should().BeEmpty();
        harness.Manager.Get(handle.ActivityId, "s1")!.Status.Should().Be(BackgroundActivityStatus.Succeeded);

        // 落盘失败也不得写 outbox：「先落盘再投递」的前提不成立（spec §19 红线 6）。
        harness.Notifications.Enqueued.Should().BeEmpty();
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
    public async Task List_ShouldBeLimitedToConfiguredLimit()
    {
        var time = new FakeTimeProvider();
        var harness = CreateHarness(
            timeProvider: time,
            options: new BackgroundActivityOptions { ListLimit = 2 });

        var oldest = await harness.Manager.StartAsync(Request("s1", title: "oldest"), "s1");
        time.Advance(TimeSpan.FromSeconds(1));
        var middle = await harness.Manager.StartAsync(Request("s1", title: "middle"), "s1");
        time.Advance(TimeSpan.FromSeconds(1));
        var newest = await harness.Manager.StartAsync(Request("s1", title: "newest"), "s1");

        var list = harness.Manager.List(new BackgroundActivityQuery(), "s1");

        // 截断必须发生在「最新在前」排序之后：保留的是最新两条，不是字典枚举顺序。
        list.Should().HaveCount(2);
        list.Select(s => s.Id).Should().Equal(newest.ActivityId, middle.ActivityId);
        list.Should().NotContain(s => s.Id == oldest.ActivityId);
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
    public async Task OtherSession_ShouldNotCancelProjectVisibleActivity()
    {
        var harness = CreateHarness();
        var handle = await harness.Manager.StartAsSystemAsync(
            Request("s1") with { Visibility = BackgroundActivityVisibility.Project });

        (await harness.Manager.CancelAsync(handle.ActivityId, BackgroundActivityCancelReason.User, "s2"))
            .Should().BeFalse();
        (await harness.Manager.WaitAsync(handle.ActivityId, TimeSpan.Zero, "s2")).Outcome
            .Should().Be(BackgroundActivityWaitOutcome.TimedOut);

        harness.Manager.Get(handle.ActivityId, "s1")!.Status.Should().Be(BackgroundActivityStatus.Running);
    }

    [Fact]
    public async Task OtherSession_ShouldNotReadProjectVisibleOutput()
    {
        var harness = CreateHarness();
        var handle = await harness.Manager.StartAsSystemAsync(
            Request("s1") with { Visibility = BackgroundActivityVisibility.Project });

        var foreignPage = await harness.Manager.ReadOutputAsync(handle.ActivityId, 0, 1024, "s2");
        foreignPage.Should()
            .Be(new BackgroundActivityOutputPage(string.Empty, 0, EndOfStream: true, Truncated: false));

        var ownerPage = await harness.Manager.ReadOutputAsync(handle.ActivityId, 0, 1024, "s1");
        ownerPage.Content.Should().Be("out");
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
    public async Task UnregisterLauncher_ShouldCallLauncherCancel()
    {
        var harness = CreateHarness();
        var handle = await harness.Manager.StartAsync(Request("s1"), "s1");

        var cancelled = await harness.Manager.UnregisterLauncherAsync(Kind);

        cancelled.Should().Be(1);
        harness.Launcher.CancelCalls.Should().ContainSingle()
            .Which.Should().Be((handle.ActivityId, BackgroundActivityCancelReason.ModuleDeactivated));
        harness.Manager.Get(handle.ActivityId, "s1")!.Status.Should().Be(BackgroundActivityStatus.Cancelled);
    }

    [Fact]
    public async Task Start_DuringUnregister_ShouldThrow()
    {
        var harness = CreateHarness();
        var running = await harness.Manager.StartAsync(Request("s1"), "s1");

        var cancelGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Launcher.CancelGate = cancelGate.Task;

        var unregistering = harness.Manager.UnregisterLauncherAsync(Kind);

        // 注销窗口内（launcher 仍被持有、尚未摘除）不得放行新活动。
        await FluentActions.Awaiting(() => harness.Manager.StartAsync(Request("s1"), "s1"))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*正在注销*");

        cancelGate.SetResult();
        (await unregistering).Should().Be(1);
        harness.Manager.Get(running.ActivityId, "s1")!.Status.Should().Be(BackgroundActivityStatus.Cancelled);
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

    // ─── 输出统计 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Snapshot_ShouldCarryOutputBytes_OnCompletion()
    {
        var harness = CreateHarness();
        var handle = await harness.Manager.StartAsync(Request("s1"), "s1");

        await handle.Reporter.AppendOutputAsync(Encoding.UTF8.GetBytes("hello"));
        await handle.Reporter.CompleteAsync(new BackgroundActivityResult(
            BackgroundActivityResultKind.Text, null, null, null, null, null));

        var snapshot = harness.Manager.Get(handle.ActivityId, "s1")!;
        snapshot.Status.Should().Be(BackgroundActivityStatus.Succeeded);
        snapshot.OutputBytes.Should().Be(5);
        snapshot.OutputTruncated.Should().BeFalse();

        // 统计不额外落盘：随终态记录一并写出。
        await harness.Manager.WaitForPersistenceAsync(handle.ActivityId);
        harness.Store.Saved.Last(s => s.Id == handle.ActivityId).OutputBytes.Should().Be(5);
    }

    [Fact]
    public async Task Snapshot_ShouldCarryTruncatedFlag_WhenLimitHit()
    {
        var harness = CreateHarness();
        var handle = await harness.Manager.StartAsync(Request("s1") with { OutputLimitBytes = 4 }, "s1");

        await handle.Reporter.AppendOutputAsync(Encoding.UTF8.GetBytes("0123456789"));
        await handle.Reporter.AppendOutputAsync(Encoding.UTF8.GetBytes("late"));

        // 截断后的实际落盘字节是上限值，而不是传入长度之和。
        var running = harness.Manager.Get(handle.ActivityId, "s1")!;
        running.OutputBytes.Should().Be(4);
        running.OutputTruncated.Should().BeTrue();

        await handle.Reporter.CompleteAsync(new BackgroundActivityResult(
            BackgroundActivityResultKind.Text, null, null, null, null, null));

        var terminal = harness.Manager.Get(handle.ActivityId, "s1")!;
        terminal.OutputBytes.Should().Be(4);
        terminal.OutputTruncated.Should().BeTrue();
    }

    [Fact]
    public async Task AppendOutput_ShouldThrottleProgressEvents_ToOnePerSecond()
    {
        var time = new FakeTimeProvider();
        var harness = CreateHarness(timeProvider: time);
        var handle = await harness.Manager.StartAsync(Request("s1"), "s1");
        await harness.Manager.WaitForPersistenceAsync(handle.ActivityId);

        // 第一次追加没有历史时刻可参照，直接发一条 progress。
        await handle.Reporter.AppendOutputAsync(Encoding.UTF8.GetBytes("a\n"));
        harness.Publisher.ActivityEvents.Count(e => e.Type == ActivityEventTypes.Progress).Should().Be(1);

        // 1 秒内的后续追加一律合并（不额外发事件）。
        await handle.Reporter.AppendOutputAsync(Encoding.UTF8.GetBytes("b\n"));
        await handle.Reporter.AppendOutputAsync(Encoding.UTF8.GetBytes("c\n"));
        harness.Publisher.ActivityEvents.Count(e => e.Type == ActivityEventTypes.Progress).Should().Be(1);

        time.Advance(TimeSpan.FromMilliseconds(500));
        await handle.Reporter.AppendOutputAsync(Encoding.UTF8.GetBytes("d\n"));
        harness.Publisher.ActivityEvents.Count(e => e.Type == ActivityEventTypes.Progress).Should().Be(1);

        // 跨过 1 秒窗口后恢复发事件：这次的事件带上累积后的完整尾部。
        time.Advance(TimeSpan.FromMilliseconds(500));
        await handle.Reporter.AppendOutputAsync(Encoding.UTF8.GetBytes("e\n"));

        var progressEvents = harness.Publisher.ActivityEvents
            .Where(e => e.Type == ActivityEventTypes.Progress)
            .ToList();
        progressEvents.Should().HaveCount(2);
        progressEvents[^1].OutputTail.Should().Be("a\nb\nc\nd\ne\n");
    }

    [Fact]
    public async Task AppendOutput_ShouldCarryBoundedTail()
    {
        var harness = CreateHarness();
        var handle = await harness.Manager.StartAsync(Request("s1"), "s1");
        await harness.Manager.WaitForPersistenceAsync(handle.ActivityId);

        // 每行 50 字符、共 200 行（10000 字符），一次性追加时必然超过 4096 字符上限。
        var lines = Enumerable.Range(0, 200)
            .Select(i => $"line-{i:D3}-{new string('x', 40)}\n")
            .ToArray();
        var fullText = string.Concat(lines);

        await handle.Reporter.AppendOutputAsync(Encoding.UTF8.GetBytes(fullText));

        var tail = harness.Publisher.ActivityEvents
            .Single(e => e.Type == ActivityEventTypes.Progress)
            .OutputTail;

        tail.Should().NotBeNull();
        tail!.Length.Should().BeLessThanOrEqualTo(4096);

        // 是「尾部」而不是头部：最后一行必须还在，开头部分必须已经被裁掉。
        fullText.Should().EndWith(tail);
        tail.Should().StartWith("line-", "按行裁剪，不能从某一行中间切开");
    }

    [Fact]
    public async Task AppendOutput_ShouldNotEmitAfterTerminal()
    {
        var time = new FakeTimeProvider();
        var harness = CreateHarness(timeProvider: time);
        var handle = await harness.Manager.StartAsync(Request("s1"), "s1");
        await handle.Reporter.AppendOutputAsync(Encoding.UTF8.GetBytes("before\n"));
        await handle.Reporter.CompleteAsync(NoResult());

        var eventsBefore = harness.Publisher.ActivityEvents.Count;

        await handle.Reporter.AppendOutputAsync(Encoding.UTF8.GetBytes("after-1\n"));
        time.Advance(TimeSpan.FromSeconds(2));
        await handle.Reporter.AppendOutputAsync(Encoding.UTF8.GetBytes("after-2\n"));

        harness.Publisher.ActivityEvents.Count.Should().Be(eventsBefore, "终态后不再发任何活动事件");
        harness.Publisher.ActivityEvents.Should().NotContain(e => e.OutputTail != null && e.OutputTail.Contains("after"));
    }

    // ─── 保留期回收 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Prune_ShouldRemoveTerminalActivitiesOlderThanCutoff()
    {
        var harness = CreateHarness();
        var handle = await harness.Manager.StartAsync(Request("s1"), "s1");
        await handle.Reporter.CompleteAsync(NoResult());

        var completedAt = harness.Manager.Get(handle.ActivityId, "s1")!.CompletedAt!.Value;

        var pruned = await harness.Manager.PruneAsync(completedAt.AddSeconds(1));

        pruned.Should().Be(1);
        harness.Manager.Get(handle.ActivityId, "s1").Should().BeNull("回收后内存表里不再有该活动");
        harness.Store.Deleted.Should().Contain(handle.ActivityId, "落盘快照必须一并删除");
        harness.Output.Deleted.Should().Contain(handle.ActivityId, "输出文件必须一并删除");
    }

    [Fact]
    public async Task Prune_ShouldKeepRunningActivities()
    {
        var harness = CreateHarness();
        var running = await harness.Manager.StartAsync(Request("s1"), "s1");

        // 截止时刻放到最远：任何「终态」都算过期，唯一能活下来的是非终态。
        var pruned = await harness.Manager.PruneAsync(DateTimeOffset.MaxValue);

        pruned.Should().Be(0);
        harness.Manager.Get(running.ActivityId, "s1").Should().NotBeNull("删掉在跑的活动等于丢掉唯一的控制入口");
        harness.Store.Deleted.Should().BeEmpty();
        harness.Output.Deleted.Should().BeEmpty();
    }

    // ─── 测试替身与工具 ───────────────────────────────────────────────────────

    private sealed class Harness
    {
        public required BackgroundActivityManager Manager { get; init; }
        public required FakeLauncher Launcher { get; init; }
        public required FakePublisher Publisher { get; init; }
        public required FakeStore Store { get; init; }
        public required FakeOutputStore Output { get; init; }
        public required FakeNotificationQueue Notifications { get; init; }
    }

    private static Harness CreateHarness(
        TimeProvider? timeProvider = null,
        BackgroundActivityOptions? options = null)
    {
        var launcher = new FakeLauncher(Kind);
        var publisher = new FakePublisher();
        var store = new FakeStore();
        var output = new FakeOutputStore();
        var notifications = new FakeNotificationQueue();
        var effectiveOptions = options ?? new BackgroundActivityOptions();

        var services = new ServiceCollection().BuildServiceProvider();

        var manager = new BackgroundActivityManager(
            publisher,
            services,
            new FixedOptionsMonitor<BackgroundActivityOptions>(effectiveOptions),
            timeProvider ?? new FakeTimeProvider(),
            store,
            output,
            notifications,
            NullLogger<BackgroundActivityManager>.Instance);

        manager.RegisterLauncher("test-module", launcher);
        return new Harness
        {
            Manager = manager,
            Launcher = launcher,
            Publisher = publisher,
            Store = store,
            Output = output,
            Notifications = notifications
        };
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

    private static BackgroundActivityResult NoResult() => new(
        BackgroundActivityResultKind.None,
        ArtifactPath: null,
        ResultSessionId: null,
        ExitCode: null,
        ErrorCode: null,
        ErrorMessage: null);

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

    private sealed class FakeNotificationQueue : IBackgroundActivityNotificationQueue
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
    {        public FakeLauncher(string kind) => Kind = kind;

        public TaskCompletionSource ExecutionSource { get; }
            = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Kind { get; }

        public BackgroundActivityCapabilities Capabilities { get; init; }
            = BackgroundActivityCapabilities.CanCancel | BackgroundActivityCapabilities.CanWait
              | BackgroundActivityCapabilities.CanStreamOutput;

        public int LaunchCount { get; private set; }

        /// <summary>取消回调的闸门（用于制造「注销进行中」窗口）。</summary>
        public Task CancelGate { get; set; } = Task.CompletedTask;

        public List<(string ActivityId, BackgroundActivityCancelReason Reason)> CancelCalls { get; } = new();

        public Task<BackgroundActivityLaunchResult> LaunchAsync(
            BackgroundActivityLaunchContext ctx,
            CancellationToken ct)
        {
            LaunchCount++;
            return Task.FromResult(new BackgroundActivityLaunchResult(ExecutionSource.Task));
        }

        public async Task CancelAsync(BackgroundActivityHandle handle, BackgroundActivityCancelReason reason)
        {
            lock (CancelCalls)
                CancelCalls.Add((handle.ActivityId, reason));

            await CancelGate;
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
        private readonly TaskCompletionSource _firstSaveStarted
            = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource? _firstSaveGate;

        public Func<BackgroundActivitySnapshot, Task>? OnSave { get; set; }

        /// <summary>第一次 Save 已开始（用于「Running 已入链并卡住」的确定性同步）。</summary>
        public Task FirstSaveStarted => _firstSaveStarted.Task;

        /// <summary>让第一次 Save 阻塞，直到 <see cref="ReleaseFirstSave"/>（模拟慢盘）。</summary>
        public void BlockFirstSave()
            => _firstSaveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseFirstSave() => _firstSaveGate?.TrySetResult();

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
            int index;
            lock (_gate)
            {
                index = _saved.Count;
                _saved.Add(snapshot);
            }

            _firstSaveStarted.TrySetResult();

            if (index == 0 && _firstSaveGate is { } gate)
                await gate.Task;

            if (OnSave is { } onSave)
                await onSave(snapshot);
        }

        public Task<BackgroundActivitySnapshot?> LoadAsync(string activityId)
        {
            lock (_gate)
                return Task.FromResult(_saved.LastOrDefault(s => s.Id == activityId));
        }

        public Task<BackgroundActivitySnapshotBatch> LoadManyAsync(
            IReadOnlyCollection<string> activityIds)
        {
            lock (_gate)
            {
                return Task.FromResult(new BackgroundActivitySnapshotBatch(
                    _saved.Where(s => activityIds.Contains(s.Id)).ToList(),
                    Array.Empty<string>()));
            }
        }

        public Task<IReadOnlyList<BackgroundActivitySnapshot>> LoadAllAsync()
        {
            lock (_gate)
                return Task.FromResult<IReadOnlyList<BackgroundActivitySnapshot>>(_saved.ToList());
        }

        public Task<bool> ReplaceIfUnchangedAsync(
            string activityId,
            BackgroundActivityStatus expectedStatus,
            string expectedHostInstanceId,
            Func<BackgroundActivitySnapshot, BackgroundActivitySnapshot> replacement)
        {
            lock (_gate)
            {
                var current = _saved.LastOrDefault(s => s.Id == activityId);
                if (current is null
                    || current.Status != expectedStatus
                    || !string.Equals(current.HostInstanceId, expectedHostInstanceId, StringComparison.Ordinal))
                {
                    return Task.FromResult(false);
                }

                _saved[_saved.IndexOf(current)] = replacement(current);
                return Task.FromResult(true);
            }
        }

        /// <summary>已删除的活动 id（保留期回收断言用）。</summary>
        public List<string> Deleted { get; } = new();

        public Task DeleteAsync(string activityId)
        {
            lock (_gate)
            {
                Deleted.Add(activityId);
                _saved.RemoveAll(s => string.Equals(s.Id, activityId, StringComparison.Ordinal));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeOutputStore : IActivityOutputStore
    {
        private readonly Dictionary<string, long> _totals = new(StringComparer.Ordinal);
        private readonly HashSet<string> _truncated = new(StringComparer.Ordinal);

        public List<(string ActivityId, byte[] Chunk)> Appends { get; } = new();

        public Dictionary<string, long> Limits { get; } = new(StringComparer.Ordinal);

        public void Configure(string activityId, long limitBytes)
        {
            lock (Limits)
                Limits[activityId] = limitBytes;
        }

        public Task<ActivityOutputAppendResult> AppendAsync(
            string activityId,
            ReadOnlyMemory<byte> chunk,
            CancellationToken ct)
        {
            lock (Appends)
            {
                Appends.Add((activityId, chunk.ToArray()));

                // 与真实存储同口径：超出上限的字节不落盘，并在越限点标记截断。
                var limit = Limits.TryGetValue(activityId, out var configured) ? configured : long.MaxValue;
                _totals.TryGetValue(activityId, out var total);
                var writable = Math.Min(chunk.Length, Math.Max(0, limit - total));
                total += writable;
                _totals[activityId] = total;

                if (writable < chunk.Length)
                    _truncated.Add(activityId);

                return Task.FromResult(new ActivityOutputAppendResult(total, _truncated.Contains(activityId)));
            }
        }

        public Task<BackgroundActivityOutputPage> ReadAsync(
            string activityId,
            long offset,
            int maxBytes,
            CancellationToken ct)
            => Task.FromResult(new BackgroundActivityOutputPage("out", offset + maxBytes, EndOfStream: false, Truncated: false));

        public Task DeleteAsync(string activityId)
        {
            lock (Deleted)
                Deleted.Add(activityId);

            return Task.CompletedTask;
        }

        /// <summary>已删除输出文件的活动 id（保留期回收断言用）。</summary>
        public List<string> Deleted { get; } = new();
    }
}

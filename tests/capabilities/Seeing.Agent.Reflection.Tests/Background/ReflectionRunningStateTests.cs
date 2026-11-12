using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Reflection.Abstractions;
using Seeing.Agent.Reflection.Background;
using Seeing.Agent.Reflection.Configuration;
using Seeing.Agent.Reflection.Core;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Reflection.Tests.Background;

/// <summary>
/// 「在复盘中」的可观测性：排队快照 + 当前执行项。
/// <para>
/// 这两条事实原先都拿不到——用户点完「立即复盘」后只能干等，无法区分
/// 「确实在跑」与「按钮没生效」。这里钉住三个语义：
/// 出队即离开队列、执行中单独登记、结束后必须清空（否则 UI 永久显示忙碌）。
/// </para>
/// </summary>
public sealed class ReflectionRunningStateTests
{
    [Fact]
    public void Snapshot_Should_Return_Queued_In_Request_Order()
    {
        var queue = new ReflectionQueue(capacity: 8);
        var later = new ReflectionRequest("ses-b", "", ReflectionTriggerKind.Idle, DateTimeOffset.UtcNow.AddMinutes(1));
        var earlier = new ReflectionRequest("ses-a", "", ReflectionTriggerKind.TurnNudge, DateTimeOffset.UtcNow);

        queue.TryEnqueue(later);
        queue.TryEnqueue(earlier);

        queue.Snapshot().Select(r => r.SessionId).Should().Equal(
            new[] { "ses-a", "ses-b" },
            "按请求产生时刻升序：用户看到的是「谁等得最久」");
    }

    [Fact]
    public void Snapshot_Should_Shrink_On_Dequeue()
    {
        var queue = new ReflectionQueue(capacity: 8);
        queue.TryEnqueue(new ReflectionRequest("ses-a", "", ReflectionTriggerKind.Manual, DateTimeOffset.UtcNow));

        queue.TryDequeue(out _).Should().BeTrue();

        queue.Snapshot().Should().BeEmpty("出队即离开队列，否则「排队中」会把正在跑的也算进去");
    }

    [Fact]
    public void Running_Should_Be_Set_Then_Cleared()
    {
        var tracker = new ReflectionRunningTracker();
        var request = new ReflectionRequest("ses-a", "", ReflectionTriggerKind.Manual, DateTimeOffset.UtcNow);

        tracker.Current.Should().BeNull();

        tracker.Begin(request);
        tracker.Current.Should().Be(request);

        tracker.End(request);
        tracker.Current.Should().BeNull();
    }

    [Fact]
    public void End_Should_Not_Clear_A_Different_Request()
    {
        // 按值比对再清：结束了 A 不该把刚登记的 B 抹掉（单循环下窗口极窄，但抹错会让 B 永久不可见）。
        var tracker = new ReflectionRunningTracker();
        var a = new ReflectionRequest("ses-a", "", ReflectionTriggerKind.Manual, DateTimeOffset.UtcNow);
        var b = new ReflectionRequest("ses-b", "", ReflectionTriggerKind.Manual, DateTimeOffset.UtcNow);

        tracker.Begin(b);
        tracker.End(a);

        tracker.Current.Should().Be(b);
    }

    [Fact]
    public async Task Status_Should_Expose_Both_Queued_And_Running()
    {
        var queue = new ReflectionQueue(capacity: 8);
        var running = new ReflectionRunningTracker();
        var activity = new ReflectionModuleActivity { IsActive = true };
        var storage = new ReflectionStorageRoot(Path.Combine(Path.GetTempPath(), "seeing-status"));

        var status = new ReflectionStatus(
            activity,
            Mock.Of<IExecutionUsageLedger>(l => l.UsedToday() == 42),
            queue,
            running,
            storage);

        var queued = new ReflectionRequest("ses-q", "", ReflectionTriggerKind.Idle, DateTimeOffset.UtcNow);
        var current = new ReflectionRequest("ses-r", "", ReflectionTriggerKind.Manual, DateTimeOffset.UtcNow);
        queue.TryEnqueue(queued);
        running.Begin(current);

        status.PendingCount.Should().Be(1);
        status.Queued.Should().ContainSingle().Which.SessionId.Should().Be("ses-q");
        status.Running.Should().Be(current);
        status.UsedTodayTokens.Should().Be(42);
        status.IsModuleActive.Should().BeTrue();
        status.StorageDirectory.Should().Be(storage.Path);
    }

    [Fact]
    public async Task Worker_Should_Clear_Running_When_Executor_Throws()
    {
        // 异常路径漏清会让记录页永久显示「复盘中」——这是最容易漏的一处。
        var queue = new ReflectionQueue(capacity: 4);
        queue.TryEnqueue(new ReflectionRequest("ses-a", "", ReflectionTriggerKind.Manual, DateTimeOffset.UtcNow));

        var running = new ReflectionRunningTracker();
        var activity = new ReflectionModuleActivity { IsActive = true };

        var options = new Mock<IOptionsMonitor<ReflectionOptions>>();
        options.SetupGet(m => m.CurrentValue).Returns(new ReflectionOptions());

        var worker = new ReflectionWorker(
            queue,
            executor: new ThrowingExecutor(),
            idleScanner: new ReflectionIdleScanner(
                queue,
                new Seeing.Session.Management.SessionActivityTracker(),
                options.Object),
            moduleActivity: activity,
            logger: NullLogger<ReflectionWorker>.Instance,
            wake: new ReflectionWakeSignal(),
            running: running);

        // 直接驱动一轮排空：StartAsync 会跑长循环，这里只需要「执行一次并观察到清理」。
        await worker.StartAsync(TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        worker.CancelInFlight();
        await worker.StopAsync(cts.Token);

        running.Current.Should().BeNull("异常路径也必须清空「执行中」登记");
    }

    private sealed class ThrowingExecutor : IReflectionExecutor
    {
        public Task<ReflectionRunResult> RunAsync(ReflectionRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("模拟执行器故障");
    }
}

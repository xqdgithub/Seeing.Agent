using FluentAssertions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Hosting.Execution;
using Xunit;

namespace Seeing.Agent.Tests.App.Execution;

public class SessionExecutionQueueTests
{
    [Fact]
    public async Task CancelAsync_CancelsCurrentExecution_ShouldAdvanceQueueToNext()
    {
        var queue = new SessionExecutionQueue();
        var first = new ExecutionRecord { ExecutionId = "a", SessionId = "s" };
        var second = new ExecutionRecord { ExecutionId = "b", SessionId = "s" };

        await queue.SubmitAsync(first);
        await queue.SubmitAsync(second);
        first.Status.Should().Be(ExecutionStatus.Pending);
        second.Status.Should().Be(ExecutionStatus.Queued);

        var cancelled = await queue.CancelAsync("a");

        cancelled.Should().Be(ExecutionCancelOutcome.CancelledNotStarted);
        first.Status.Should().Be(ExecutionStatus.Cancelled);
        queue.CurrentExecution.Should().Be(second);
        queue.CurrentExecution!.Status.Should().Be(ExecutionStatus.Pending);
        queue.QueueLength.Should().Be(0);
        queue.HasActiveExecution.Should().BeTrue();
    }

    [Fact]
    public async Task StartAsync_AfterCurrentExecutionCancelled_ShouldNotReviveIt()
    {
        var queue = new SessionExecutionQueue();
        var record = new ExecutionRecord { ExecutionId = "a", SessionId = "s" };

        await queue.SubmitAsync(record);
        await queue.CancelAsync("a");

        var started = await queue.StartAsync();

        started.Should().BeFalse();
        record.Status.Should().Be(ExecutionStatus.Cancelled);
    }

    [Fact]
    public async Task StartAsync_ShouldOnlyStartPendingExecution()
    {
        var queue = new SessionExecutionQueue();
        var record = new ExecutionRecord { ExecutionId = "a", SessionId = "s" };

        await queue.SubmitAsync(record);
        var started = await queue.StartAsync();

        started.Should().BeTrue();
        record.Status.Should().Be(ExecutionStatus.Running);

        var again = await queue.StartAsync();
        again.Should().BeFalse();
        record.Status.Should().Be(ExecutionStatus.Running);
    }

    [Fact]
    public async Task SubmitAsync_ShouldAttachCtsToCurrentAndQueuedRecords()
    {
        var queue = new SessionExecutionQueue();
        var first = new ExecutionRecord { ExecutionId = "a", SessionId = "s" };
        var second = new ExecutionRecord { ExecutionId = "b", SessionId = "s" };

        await queue.SubmitAsync(first);
        await queue.SubmitAsync(second);

        // 入队即创建 CTS：排队项也具备可取消能力
        first.Cts.Should().NotBeNull();
        second.Cts.Should().NotBeNull();
        first.Cts!.Token.IsCancellationRequested.Should().BeFalse();
        second.Cts!.Token.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task CancelAsync_AfterStart_ShouldCancelRecordToken()
    {
        // 窗口期：StartAsync 之后、执行体读取 token 之前取消，快照 token 必须已观察取消
        var queue = new SessionExecutionQueue();
        var record = new ExecutionRecord { ExecutionId = "a", SessionId = "s" };

        await queue.SubmitAsync(record);
        await queue.StartAsync();
        var token = record.Cts!.Token;

        var cancelled = await queue.CancelAsync("a");

        cancelled.Should().Be(ExecutionCancelOutcome.CancelledStarted);
        token.IsCancellationRequested.Should().BeTrue();
        record.Status.Should().Be(ExecutionStatus.Cancelled);
    }

    [Fact]
    public async Task CancelAsync_ShouldNotAffectOtherRecordToken()
    {
        var queue = new SessionExecutionQueue();
        var first = new ExecutionRecord { ExecutionId = "a", SessionId = "s" };
        var second = new ExecutionRecord { ExecutionId = "b", SessionId = "s" };

        await queue.SubmitAsync(first);
        await queue.SubmitAsync(second);
        await queue.StartAsync();

        var firstToken = first.Cts!.Token;
        var secondToken = second.Cts!.Token;

        await queue.CancelAsync("a");

        // exec1 取消不得污染 exec2 的令牌（闪断根因回归）
        firstToken.IsCancellationRequested.Should().BeTrue();
        secondToken.IsCancellationRequested.Should().BeFalse();
        queue.CurrentExecution.Should().Be(second);
    }

    [Fact]
    public async Task CancelAsync_QueuedExecution_ShouldCancelItsToken()
    {
        var queue = new SessionExecutionQueue();
        var first = new ExecutionRecord { ExecutionId = "a", SessionId = "s" };
        var second = new ExecutionRecord { ExecutionId = "b", SessionId = "s" };

        await queue.SubmitAsync(first);
        await queue.SubmitAsync(second);
        var queuedToken = second.Cts!.Token;

        var cancelled = await queue.CancelAsync("b");

        cancelled.Should().Be(ExecutionCancelOutcome.CancelledNotStarted);
        queuedToken.IsCancellationRequested.Should().BeTrue();
        second.Status.Should().Be(ExecutionStatus.Cancelled);
        queue.QueueLength.Should().Be(0);
    }

    [Fact]
    public async Task CompleteAsync_AfterCancel_ShouldNotThrowAndReleaseCts()
    {
        var queue = new SessionExecutionQueue();
        var record = new ExecutionRecord { ExecutionId = "a", SessionId = "s" };

        await queue.SubmitAsync(record);
        await queue.StartAsync();
        await queue.CancelAsync("a");

        // 取消后执行体的 finally 仍会调用 CompleteAsync；不得因 CTS 释放时序抛 ObjectDisposedException
        var act = async () => await queue.CompleteAsync(record, ExecutionStatus.Cancelled);

        await act.Should().NotThrowAsync();
        record.Cts.Should().BeNull();
    }

    [Fact]
    public async Task CancelAsync_CurrentPendingBeforeStart_ReturnsNotStarted()
    {
        // 启动窗口：Submit 后、StartAsync 前取消——判定必须为「未启动」，由调用方发布终态
        var queue = new SessionExecutionQueue();
        var record = new ExecutionRecord { ExecutionId = "a", SessionId = "s" };

        await queue.SubmitAsync(record);

        var outcome = await queue.CancelAsync("a");

        outcome.Should().Be(ExecutionCancelOutcome.CancelledNotStarted);
        record.Status.Should().Be(ExecutionStatus.Cancelled);
        record.Cts.Should().BeNull();
    }

    [Fact]
    public async Task CancelAsync_UnknownOrTerminal_ReturnsNotFound()
    {
        var queue = new SessionExecutionQueue();
        var record = new ExecutionRecord { ExecutionId = "a", SessionId = "s" };

        (await queue.CancelAsync("missing")).Should().Be(ExecutionCancelOutcome.NotFound);

        await queue.SubmitAsync(record);
        await queue.CancelAsync("a");
        // 已终态：再次取消不得成功（避免重复发布终态）
        (await queue.CancelAsync("a")).Should().Be(ExecutionCancelOutcome.NotFound);
    }

    [Fact]
    public async Task GetSnapshot_ShouldReflectActiveAndQueuedCounts()
    {
        var queue = new SessionExecutionQueue();
        var first = new ExecutionRecord { ExecutionId = "a", SessionId = "s" };
        var second = new ExecutionRecord { ExecutionId = "b", SessionId = "s" };

        var empty = queue.GetSnapshot();
        empty.HasActiveExecution.Should().BeFalse();
        empty.HasQueued.Should().BeFalse();
        empty.QueueLength.Should().Be(0);

        await queue.SubmitAsync(first);
        await queue.SubmitAsync(second);

        var active = queue.GetSnapshot();
        active.HasActiveExecution.Should().BeTrue();
        active.HasQueued.Should().BeTrue();
        active.QueueLength.Should().Be(1);
    }

    [Fact]
    public async Task Dispose_WhileExecutionStarted_ShouldNotDisposeInFlightCts()
    {
        // I4 回归：Dispose 在途执行时仅取消，不得释放执行体仍持有的 CTS
        var queue = new SessionExecutionQueue();
        var record = new ExecutionRecord { ExecutionId = "a", SessionId = "s" };

        await queue.SubmitAsync(record);
        await queue.StartAsync();
        var token = record.Cts!.Token;

        queue.Dispose();

        token.IsCancellationRequested.Should().BeTrue();
        record.Cts.Should().NotBeNull();
        var act = () => token.Register(() => { });
        act.Should().NotThrow();

        // 执行体 finally 仍能释放 CTS
        await queue.CompleteAsync(record, ExecutionStatus.Cancelled);
        record.Cts.Should().BeNull();
    }

    [Fact]
    public async Task Dispose_QueuedExecution_ShouldCancelAndDisposeItsCts()
    {
        var queue = new SessionExecutionQueue();
        var first = new ExecutionRecord { ExecutionId = "a", SessionId = "s" };
        var queued = new ExecutionRecord { ExecutionId = "b", SessionId = "s" };

        await queue.SubmitAsync(first);
        await queue.SubmitAsync(queued);
        await queue.StartAsync();

        queue.Dispose();

        // 排队项从未被消费，可安全取消并释放
        queued.Status.Should().Be(ExecutionStatus.Cancelled);
        queued.Cts.Should().BeNull();
    }

    [Fact]
    public async Task SubmitAsync_AfterDispose_ShouldThrowObjectDisposed()
    {
        var queue = new SessionExecutionQueue();
        queue.Dispose();

        var act = async () => await queue.SubmitAsync(new ExecutionRecord { ExecutionId = "a", SessionId = "s" });

        // Submit 循环据此重取字典中的新队列，避免记录落入孤儿队列
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task TryRetireIfIdle_WhenActive_ShouldReturnFalseAndKeepAccepting()
    {
        var queue = new SessionExecutionQueue();
        var record = new ExecutionRecord { ExecutionId = "a", SessionId = "s" };
        await queue.SubmitAsync(record);

        // 有在途项：拒绝退休，调用方据此放回字典/取消
        queue.TryRetireIfIdle().Should().BeFalse();

        await queue.StartAsync();
        var token = record.Cts!.Token;
        (await queue.CancelAsync("a")).Should().Be(ExecutionCancelOutcome.CancelledStarted);
        token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task TryRetireIfIdle_WhenEmpty_ShouldRetireAndRejectSubmit()
    {
        var queue = new SessionExecutionQueue();

        queue.TryRetireIfIdle().Should().BeTrue();
        queue.TryRetireIfIdle().Should().BeTrue();

        var act = async () => await queue.SubmitAsync(new ExecutionRecord { ExecutionId = "a", SessionId = "s" });
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }
}

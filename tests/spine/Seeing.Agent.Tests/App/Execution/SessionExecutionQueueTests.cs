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

        cancelled.Should().BeTrue();
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

        cancelled.Should().BeTrue();
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

        cancelled.Should().BeTrue();
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
}

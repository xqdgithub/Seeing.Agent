using FluentAssertions;
using Seeing.Agent.App.Execution;
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
}

using FluentAssertions;
using Seeing.Agent.Reflection.Abstractions;
using Seeing.Agent.Reflection.Core;
using Xunit;

namespace Seeing.Agent.Reflection.Tests.Queue;

/// <summary>复盘队列的去重、覆盖、容量与阻塞语义。</summary>
public sealed class ReflectionQueueTests
{
    private static ReflectionRequest Request(
        string sessionId,
        string executionId,
        ReflectionTriggerKind kind = ReflectionTriggerKind.TurnNudge)
        => new(sessionId, executionId, kind, DateTimeOffset.UtcNow);

    [Fact]
    public void TryEnqueue_Should_Deduplicate_Same_Session()
    {
        var queue = new ReflectionQueue(capacity: 8);

        queue.TryEnqueue(Request("s1", "e1")).Should().BeTrue();
        queue.TryEnqueue(Request("s1", "e1")).Should().BeFalse();

        queue.PendingCount.Should().Be(1);
    }

    [Fact]
    public void TryEnqueue_Should_Update_ExecutionId_For_Pending_Session()
    {
        var queue = new ReflectionQueue(capacity: 8);

        queue.TryEnqueue(Request("s1", "e1")).Should().BeTrue();
        queue.TryEnqueue(Request("s1", "e2")).Should().BeFalse();

        queue.PendingCount.Should().Be(1);
        queue.TryDequeue(out var request).Should().BeTrue();
        request.ExecutionId.Should().Be("e2");
    }

    [Fact]
    public void TryEnqueue_Should_Return_False_When_Full()
    {
        var queue = new ReflectionQueue(capacity: 1);

        queue.TryEnqueue(Request("s1", "e1")).Should().BeTrue();
        queue.TryEnqueue(Request("s2", "e2")).Should().BeFalse();

        queue.PendingCount.Should().Be(1);
    }

    [Fact]
    public void TryDequeue_After_Dequeue_Should_Allow_Reenqueue_Same_Session()
    {
        var queue = new ReflectionQueue(capacity: 8);
        queue.TryEnqueue(Request("s1", "e1")).Should().BeTrue();
        queue.TryDequeue(out _).Should().BeTrue();

        queue.TryEnqueue(Request("s1", "e2")).Should().BeTrue();
        queue.PendingCount.Should().Be(1);
    }

    [Fact]
    public async Task DequeueAsync_Should_Wait_Until_Enqueued()
    {
        var queue = new ReflectionQueue(capacity: 8);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var waiting = queue.DequeueAsync(cts.Token).AsTask();
        waiting.IsCompleted.Should().BeFalse();

        queue.TryEnqueue(Request("s1", "e1")).Should().BeTrue();

        var completed = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(5), cts.Token));
        completed.Should().BeSameAs(waiting);

        var request = await waiting;
        request.SessionId.Should().Be("s1");
    }
}

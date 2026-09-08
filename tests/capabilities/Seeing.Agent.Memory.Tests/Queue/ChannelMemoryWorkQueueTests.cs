using FluentAssertions;
using Seeing.Agent.Memory.Core.Models;
using Seeing.Agent.Memory.Core.Queue;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Queue;

public class ChannelMemoryWorkQueueTests
{
    [Fact]
    public void TryEnqueue_WhenUnderCapacity_ShouldReturnTrueAndIncreaseCount()
    {
        var q = new ChannelMemoryWorkQueue(capacity: 2);
        q.TryEnqueue(NewBatch("a")).Should().BeTrue();
        q.Count.Should().Be(1);
    }

    [Fact]
    public void TryEnqueue_WhenFull_ShouldReturnFalse()
    {
        var q = new ChannelMemoryWorkQueue(capacity: 1);
        q.TryEnqueue(NewBatch("a")).Should().BeTrue();
        q.TryEnqueue(NewBatch("b")).Should().BeFalse();
        q.Count.Should().Be(1);
    }

    private static MemoryBatch NewBatch(string id)
    {
        var c = new MemoryCandidate(id, "s1", null, MemorySource.Chat, null, "hello world content here", DateTimeOffset.UtcNow);
        return new MemoryBatch(id, "s1", new[] { c }, DateTimeOffset.UtcNow);
    }
}

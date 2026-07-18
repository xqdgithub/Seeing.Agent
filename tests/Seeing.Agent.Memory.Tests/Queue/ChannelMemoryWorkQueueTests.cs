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
        var c = NewCandidate("a");
        q.TryEnqueue(c).Should().BeTrue();
        q.Count.Should().Be(1);
    }

    [Fact]
    public void TryEnqueue_WhenFull_ShouldReturnFalse()
    {
        var q = new ChannelMemoryWorkQueue(capacity: 1);
        q.TryEnqueue(NewCandidate("a")).Should().BeTrue();
        q.TryEnqueue(NewCandidate("b")).Should().BeFalse();
        q.Count.Should().Be(1);
    }

    private static MemoryCandidate NewCandidate(string id) =>
        new(id, "s1", null, MemorySource.Chat, null, "hello world content here", DateTimeOffset.UtcNow);
}

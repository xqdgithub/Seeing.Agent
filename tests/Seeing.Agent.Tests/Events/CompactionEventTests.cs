using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Xunit;

namespace Seeing.Agent.Tests.Events;

public class CompactionEventTests
{
    [Fact]
    public void CompactionStartedEvent_ShouldUseStringType()
    {
        var evt = new CompactionStartedEvent
        {
            SessionId = "s1",
            Reason = "manual"
        };

        evt.Type.Should().Be("compaction.started");
        evt.SessionId.Should().Be("s1");
        evt.Reason.Should().Be("manual");
    }

    [Fact]
    public void CompactionCompletedEvent_ShouldCarryTokenStats()
    {
        var evt = new CompactionCompletedEvent
        {
            SessionId = "s1",
            TokensBefore = 5000,
            TokensAfter = 1200,
            MessagesRemoved = 3
        };

        evt.Type.Should().Be("compaction.completed");
        evt.TokensBefore.Should().Be(5000);
        evt.TokensAfter.Should().Be(1200);
        evt.MessagesRemoved.Should().Be(3);
    }
}
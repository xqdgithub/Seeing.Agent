using FluentAssertions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Core.Execution;
using Xunit;

namespace Seeing.Agent.Tests.Execution;

public class OverviewDtoTests
{
    [Fact]
    public void Overview_ShouldHaveLastExecutionAndPendingInputCount()
    {
        var o = new SessionExecutionOverview { LastExecution = null, PendingInputCount = 3 };
        o.PendingInputCount.Should().Be(3);
        typeof(SessionExecutionOverview).GetProperty("LastExecution").Should().NotBeNull();
        typeof(SessionExecutionOverview).GetProperty("QueueLength").Should().BeNull();
        typeof(SessionExecutionOverview).GetProperty("QueuedExecutions").Should().BeNull();
    }

    [Fact]
    public void ExecutionRecord_ShouldNotHaveQueuePosition()
    {
        typeof(ExecutionRecord).GetProperty("QueuePosition").Should().BeNull();
    }

    [Fact]
    public void ExecutionOptions_ShouldNotHaveMaxQueueSizePerSession()
    {
        typeof(ExecutionOptions).GetProperty("MaxQueueSizePerSession").Should().BeNull();
    }
}

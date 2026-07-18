using FluentAssertions;
using Seeing.Agent.Core.Events;
using Xunit;

namespace Seeing.Agent.Tests.Events;

public class TaskEventProjectorTests
{
    [Fact]
    public void Project_ToolCallRunning_ShouldEmitTaskProgress()
    {
        var ctx = new TaskProjectionContext("parent", "child1", "tc1", false, "explore", "Find auth");
        var projector = new TaskEventProjector();
        var child = new ToolCallEvent
        {
            SessionId = "child1",
            Type = MessageEventType.ToolCallRunning,
            ToolCallId = "x",
            ToolName = "grep",
            Status = ToolCallStatus.Running
        };

        var projected = projector.Project(child, ctx).ToList();
        projected.Should().ContainSingle()
            .Which.Should().BeOfType<TaskProgressEvent>()
            .Which.ToolName.Should().Be("grep");
    }

    [Fact]
    public void Project_StreamDelta_ShouldNotMirrorFullContent()
    {
        var ctx = new TaskProjectionContext("parent", "child1", "tc1", false, "explore", "t");
        var projector = new TaskEventProjector();
        var delta = new StreamDeltaEvent
        {
            SessionId = "child1",
            ContentDelta = new string('a', 5000)
        };

        projector.Project(delta, ctx).Should().BeEmpty();
    }
}

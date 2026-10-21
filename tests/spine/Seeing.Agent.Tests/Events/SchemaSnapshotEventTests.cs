using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Tools;
using Xunit;

namespace Seeing.Agent.Tests.Events;

public class SchemaSnapshotEventTests
{
    [Fact]
    public void SchemaSnapshotEvent_ShouldUseStringType()
    {
        var evt = new SchemaSnapshotEvent
        {
            SessionId = "s1",
            ExecutionId = "e1"
        };

        evt.Type.Should().Be("schema.snapshot");
        evt.SessionId.Should().Be("s1");
        evt.ExecutionId.Should().Be("e1");
    }

    [Fact]
    public void SchemaSnapshotEvent_ShouldCarryToolAndSectionIds()
    {
        var evt = new SchemaSnapshotEvent
        {
            SessionId = "s1",
            ExecutionId = "e1",
            ToolIds = new[] { "read", "write" },
            SectionIds = new[] { "tools", "settings" }
        };

        evt.ToolIds.Should().BeEquivalentTo("read", "write");
        evt.SectionIds.Should().BeEquivalentTo("tools", "settings");
    }

    [Fact]
    public void SchemaSnapshotEvent_ShouldImplementIMessageEvent()
    {
        var timestamp = new DateTime(2026, 9, 8, 12, 0, 0);
        IMessageEvent evt = new SchemaSnapshotEvent
        {
            SessionId = "s1",
            LoopId = "loop-1",
            ExecutionId = "e1",
            Timestamp = timestamp
        };

        evt.SessionId.Should().Be("s1");
        evt.LoopId.Should().Be("loop-1");
        evt.Timestamp.Should().Be(timestamp);
        evt.Type.Should().Be(MessageEventType.SchemaSnapshot);
    }

    [Fact]
    public void SchemaSnapshotEvent_ShouldCarryExposureCounts()
    {
        var evt = new SchemaSnapshotEvent
        {
            SessionId = "s1",
            ExecutionId = "e1",
            ExposureMode = ToolExposurePolicyMode.Tiered,
            InlineToolCount = 12,
            DeferredToolCount = 39,
            EstimatedDefinitionTokens = 4400,
            CatalogTokens = 800
        };

        evt.ExposureMode.Should().Be(ToolExposurePolicyMode.Tiered);
        evt.InlineToolCount.Should().Be(12);
        evt.DeferredToolCount.Should().Be(39);
        evt.EstimatedDefinitionTokens.Should().Be(4400);
        evt.CatalogTokens.Should().Be(800);
    }
}

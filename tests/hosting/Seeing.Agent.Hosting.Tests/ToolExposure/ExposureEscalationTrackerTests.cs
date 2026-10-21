using FluentAssertions;
using Seeing.Agent.Hosting.Execution.ToolExposure;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.ToolExposure;

public class ExposureEscalationTrackerTests
{
    [Fact]
    public void ShouldEscalateByToolCount()
    {
        ExposureEscalationTracker.ShouldEscalate(
            toolCount: 30, schemaTokens: 3000, alreadyEscalated: false,
            escalateToolCount: 30, escalateSchemaTokens: 8000)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldEscalateByTokens()
    {
        ExposureEscalationTracker.ShouldEscalate(
            toolCount: 5, schemaTokens: 8000, alreadyEscalated: false,
            escalateToolCount: 30, escalateSchemaTokens: 8000)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldStayAllBelowBoth()
    {
        ExposureEscalationTracker.ShouldEscalate(
            toolCount: 11, schemaTokens: 4400, alreadyEscalated: false,
            escalateToolCount: 30, escalateSchemaTokens: 8000)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldStayEscalatedOnceRaised()
    {
        ExposureEscalationTracker.ShouldEscalate(
            toolCount: 1, schemaTokens: 1, alreadyEscalated: true,
            escalateToolCount: 30, escalateSchemaTokens: 8000)
            .Should().BeTrue();
    }
}

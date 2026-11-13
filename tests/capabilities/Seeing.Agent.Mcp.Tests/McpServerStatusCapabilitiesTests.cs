using FluentAssertions;
using Seeing.Agent.Abstractions.Mcp;
using Xunit;

namespace Seeing.Agent.Mcp.Tests;

public class McpServerStatusCapabilitiesTests
{
    [Fact]
    public void Clone_Preserves_Capabilities_And_Optional_Counts()
    {
        var caps = new McpServerCapabilitiesInfo(
            Tools: true, Resources: true, ResourcesSubscribe: false, ResourcesListChanged: false,
            Prompts: true, PromptsListChanged: false, SkillsExtension: true, SkillsDirectoryRead: false);
        var status = new McpServerStatus(
            "demo", McpConnectionState.Connected, config: null, McpServerPriority.Normal,
            toolCount: 2, toolNames: new[] { "a", "b" },
            createdAt: DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            lastConnectedAt: null, lastErrorAt: null, lastError: null,
            reconnectAttempts: 0, activePolicy: null,
            capabilities: caps, resourceCount: 3, promptCount: 1, skillCount: 2);

        var clone = status.Clone();
        clone.Capabilities.Should().Be(caps);
        clone.ResourceCount.Should().Be(3);
        clone.PromptCount.Should().Be(1);
        clone.SkillCount.Should().Be(2);
    }
}

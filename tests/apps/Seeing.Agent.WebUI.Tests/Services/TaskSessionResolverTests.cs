using FluentAssertions;
using Moq;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Tests.Services;

public class TaskSessionResolverTests
{
    private static SessionData CreateChild(string childId, string? originToolCallId)
    {
        var child = SessionData.Create("p1", "explore");
        child.Id = childId;
        child.Kind = SessionKind.SubAgent;
        if (!string.IsNullOrEmpty(originToolCallId))
            child.Metadata[SessionMetadataKeys.OriginToolCallId] = originToolCallId;
        return child;
    }

    [Fact]
    public async Task ResolveTaskIdAsync_ToolCallAlreadyHasTaskId_ShouldReturnDirectly()
    {
        var gm = new Mock<ISessionGroupManager>();
        var resolver = new TaskSessionResolver(gm.Object);
        var toolCall = new SessionToolCall { Id = "call-1", TaskId = "task-9" };

        var result = await resolver.ResolveTaskIdAsync("parent1", toolCall);

        result.Should().Be("task-9");
        gm.Verify(m => m.ListChildrenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveTaskIdAsync_Match_ShouldResolveByOriginToolCallId()
    {
        var child = CreateChild("child1", "call-1");
        var gm = new Mock<ISessionGroupManager>();
        gm.Setup(m => m.ListChildrenAsync("parent1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { child });
        var resolver = new TaskSessionResolver(gm.Object);
        var toolCall = new SessionToolCall { Id = "call-1" };

        var result = await resolver.ResolveTaskIdAsync("parent1", toolCall);

        result.Should().Be("child1");
    }

    [Fact]
    public async Task ResolveTaskIdAsync_NoMatch_ShouldReturnNull()
    {
        var gm = new Mock<ISessionGroupManager>();
        gm.Setup(m => m.ListChildrenAsync("parent1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SessionData>());
        var resolver = new TaskSessionResolver(gm.Object);
        var toolCall = new SessionToolCall { Id = "call-9" };

        var result = await resolver.ResolveTaskIdAsync("parent1", toolCall);

        result.Should().BeNull();
    }
}

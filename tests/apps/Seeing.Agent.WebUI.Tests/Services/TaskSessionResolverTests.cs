using FluentAssertions;
using Moq;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Tests.Services;

public class TaskSessionResolverTests
{
    private static SessionData CreateChild(string childId, string parentId, string? originToolCallId)
    {
        var child = SessionData.Create("p1", "explore");
        child.Id = childId;
        child.Kind = SessionKind.SubAgent;
        child.ParentSessionId = parentId;
        if (!string.IsNullOrEmpty(originToolCallId))
            child.Metadata[SessionMetadataKeys.OriginToolCallId] = originToolCallId;
        return child;
    }

    [Fact]
    public async Task ResolveTaskIdAsync_ToolCallAlreadyHasTaskId_ShouldReturnDirectly()
    {
        var sm = new Mock<ISessionManager>();
        var resolver = new TaskSessionResolver(sm.Object);
        var toolCall = new SessionToolCall { Id = "call-1", TaskId = "task-9" };

        var result = await resolver.ResolveTaskIdAsync("parent1", toolCall);

        result.Should().Be("task-9");
        sm.Verify(m => m.ListChildrenAsync(It.IsAny<string>(), It.IsAny<SessionKind>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveTaskIdAsync_MemoryCacheMatch_ShouldResolveByOriginToolCallId()
    {
        var child = CreateChild("child1", "parent1", "call-1");
        var sm = new Mock<ISessionManager>();
        sm.Setup(m => m.ListChildrenAsync("parent1", SessionKind.SubAgent, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(new[] { child }));
        var resolver = new TaskSessionResolver(sm.Object);
        var toolCall = new SessionToolCall { Id = "call-1" };

        var result = await resolver.ResolveTaskIdAsync("parent1", toolCall);

        result.Should().Be("child1");
        sm.Verify(m => m.LoadChildrenFromStorageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveTaskIdAsync_ColdCacheFallback_ShouldQueryDiskWhenNoMemoryMatch()
    {
        var child = CreateChild("child1", "parent1", "call-1");
        var sm = new Mock<ISessionManager>();
        sm.Setup(m => m.ListChildrenAsync("parent1", SessionKind.SubAgent, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(Array.Empty<SessionData>()));
        sm.Setup(m => m.LoadChildrenFromStorageAsync("parent1", It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(new[] { child }));
        var resolver = new TaskSessionResolver(sm.Object);
        var toolCall = new SessionToolCall { Id = "call-1" };

        var result = await resolver.ResolveTaskIdAsync("parent1", toolCall);

        result.Should().Be("child1");
    }

    [Fact]
    public async Task ResolveTaskIdAsync_NoMatch_ShouldReturnNull()
    {
        var sm = new Mock<ISessionManager>();
        sm.Setup(m => m.ListChildrenAsync("parent1", SessionKind.SubAgent, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(Array.Empty<SessionData>()));
        sm.Setup(m => m.LoadChildrenFromStorageAsync("parent1", It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(Array.Empty<SessionData>()));
        var resolver = new TaskSessionResolver(sm.Object);
        var toolCall = new SessionToolCall { Id = "call-9" };

        var result = await resolver.ResolveTaskIdAsync("parent1", toolCall);

        result.Should().BeNull();
    }
}

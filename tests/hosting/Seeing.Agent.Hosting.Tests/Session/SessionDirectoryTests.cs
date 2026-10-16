using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Core;
using Seeing.Agent.Llm;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.Session;

public class SessionDirectoryTests
{
    [Fact]
    public void SessionDirectory_ShouldImplement_ISessionDirectory()
    {
        typeof(ISessionDirectory).IsAssignableFrom(typeof(SessionDirectory)).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_ShouldStopRuntimeBeforeRemoving()
    {
        var calls = new List<string>();
        var runtime = new Mock<ISessionRuntime>();
        runtime.Setup(r => r.StopAsync("s1", It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("stop"))
            .ReturnsAsync(1);
        var groupManager = new Mock<ISessionGroupManager>();
        groupManager.Setup(g => g.RemoveSessionAsync("s1", It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("remove"))
            .Returns(Task.CompletedTask);

        var directory = new SessionDirectory(
            new Mock<ISessionManager>().Object,
            groupManager.Object,
            new AgentSelectionResolver(new Mock<IAgentRuntimeManager>().Object),
            new Mock<IModelManager>().Object,
            runtime.Object,
            NullLogger<SessionDirectory>.Instance);

        await directory.DeleteAsync("s1", TestContext.Current.CancellationToken);

        calls.Should().ContainInOrder("stop", "remove");
        runtime.Verify(r => r.StopAsync("s1", It.IsAny<CancellationToken>()), Times.Once);
        groupManager.Verify(g => g.RemoveSessionAsync("s1", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static SessionDirectory NewDirectory(
        Mock<ISessionManager> sessionManager,
        Mock<ISessionGroupManager> groupManager,
        Mock<ISessionRuntime> runtime) =>
        new(
            sessionManager.Object,
            groupManager.Object,
            new AgentSelectionResolver(new Mock<IAgentRuntimeManager>().Object),
            new Mock<IModelManager>().Object,
            runtime.Object,
            NullLogger<SessionDirectory>.Instance);

    [Fact]
    public async Task ArchiveAsync_ShouldArchiveWholeGroup_WhenSessionHasGroup()
    {
        var sessionManager = new Mock<ISessionManager>();
        var groupManager = new Mock<ISessionGroupManager>();
        var runtime = new Mock<ISessionRuntime>();

        groupManager.Setup(g => g.GetGroupForSessionAsync("s1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionGroup { Id = "g1", AnchorSessionId = "s1" });
        groupManager.Setup(g => g.ArchiveGroupAsync("g1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var directory = NewDirectory(sessionManager, groupManager, runtime);
        await directory.ArchiveAsync("s1", TestContext.Current.CancellationToken);

        runtime.Verify(r => r.StopAsync("s1", It.IsAny<CancellationToken>()), Times.Once);
        groupManager.Verify(g => g.ArchiveGroupAsync("g1", It.IsAny<CancellationToken>()), Times.Once);
        sessionManager.Verify(m => m.ArchiveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ArchiveAsync_ShouldArchiveSession_WhenNoGroup()
    {
        var sessionManager = new Mock<ISessionManager>();
        var groupManager = new Mock<ISessionGroupManager>();
        var runtime = new Mock<ISessionRuntime>();

        groupManager.Setup(g => g.GetGroupForSessionAsync("s1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionGroup?)null);
        sessionManager.Setup(m => m.ArchiveAsync("s1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var directory = NewDirectory(sessionManager, groupManager, runtime);
        await directory.ArchiveAsync("s1", TestContext.Current.CancellationToken);

        sessionManager.Verify(m => m.ArchiveAsync("s1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListSummariesAsync_ShouldDelegateToGroupManager()
    {
        var sessionManager = new Mock<ISessionManager>();
        var groupManager = new Mock<ISessionGroupManager>();
        var runtime = new Mock<ISessionRuntime>();

        groupManager.Setup(g => g.ListAnchorSummariesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SessionSummary> { new() { Id = "s1", Title = "t" } });

        var directory = NewDirectory(sessionManager, groupManager, runtime);
        var result = await directory.ListSummariesAsync(TestContext.Current.CancellationToken);

        result.Should().ContainSingle(s => s.Id == "s1");
    }
}

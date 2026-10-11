using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Core;
using Seeing.Agent.Hosting;
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
}
